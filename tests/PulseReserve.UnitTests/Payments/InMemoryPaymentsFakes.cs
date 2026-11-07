using System.Reflection;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Persistence;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// A small in-memory model of the order/attempt store that follows the same rules as the PostgreSQL implementation
/// (one in-flight attempt per order, one attempt per idempotency key, verdict recorded once, a late success allowed
/// for a cancelled attempt, finalisation idempotent). It exists so service-level behaviour (provider fixed on the
/// attempt, duplicate callbacks, late success) can be tested without a database; the DB-level guarantees are covered by
/// the integration suite.
/// </summary>
internal sealed class InMemoryOrderStore : IOrderStore
{
    public sealed class AttemptRow
    {
        public Guid Id { get; init; }
        public Guid OrderId { get; init; }
        public int Number { get; init; }
        public string Key { get; init; } = string.Empty;
        public string Status { get; set; } = PaymentAttemptStatus.Initiated;
        public string Provider { get; init; } = string.Empty;
        public bool IsSimulated { get; init; }
        public string? Reference { get; set; }
        public string? CheckoutUrl { get; set; }
    }

    public Order Order { get; set; }
    public List<AttemptRow> Attempts { get; } = [];
    public int PaidTransitions { get; private set; }
    public int RefundRequiredTransitions { get; private set; }
    public int FinalizeCalls { get; private set; }
    public List<string> Calls { get; } = [];

    /// <summary>The ambient transition context as each state-changing method saw it (what the real store hands to the database).</summary>
    public List<(string Method, TransitionInfo? Info)> Seen { get; } = [];

    /// <summary>One entry per payment exception (verified success for an attempt recorded as Failed), unique per attempt as in SQL.</summary>
    public List<(Guid AttemptId, string? EventId)> Exceptions { get; } = [];

    public InMemoryOrderStore(string currency = "USD", decimal total = 91m)
    {
        Order = new Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), OrderStatus.PendingPayment, currency, total,
            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10), [], null);
    }

    public Guid CustomerId => Order.CustomerId;

    /// <summary>When set, looking an attempt up fails as a database outage would (the webhook must then not be acknowledged).</summary>
    public bool LookupsFail { get; set; }

    /// <summary>When set, the order cannot be loaded (our own data is inconsistent), although its attempt exists.</summary>
    public bool OrderUnreadable { get; set; }

    private Order Snapshot()
    {
        var latest = Attempts.OrderByDescending(a => a.Number).FirstOrDefault();
        return Order with { Payment = latest is null ? null : ToModel(latest) };
    }

    private static PaymentAttempt ToModel(AttemptRow a) => new(
        a.Id, a.Number, a.Status, a.Provider, a.IsSimulated, a.Reference, DateTime.UtcNow, null, a.CheckoutUrl);

    public Task<Order?> GetAsync(Guid orderId, Guid? customerId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Order?>(!OrderUnreadable && orderId == Order.OrderId && (customerId is null || customerId == Order.CustomerId) ? Snapshot() : null);

    public Task<PaymentAttempt?> FindAttemptByKeyAsync(Guid orderId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(Attempts.Where(a => a.OrderId == orderId && a.Key == idempotencyKey).Select(ToModel).FirstOrDefault());

    public Task<BeginPaymentResult> BeginPaymentAsync(
        Guid orderId, Guid customerId, string idempotencyKey, string provider, bool isSimulated, CancellationToken cancellationToken = default)
    {
        Calls.Add($"Begin:{provider}");
        Seen.Add(("BeginPayment", FinancialTransitionContext.Current));
        if (orderId != Order.OrderId || customerId != Order.CustomerId)
            return Task.FromResult(new BeginPaymentResult(BeginPaymentOutcome.OrderNotFound, null, null));

        var existing = Attempts.FirstOrDefault(a => a.Key == idempotencyKey);
        if (existing is not null)
            return Task.FromResult(new BeginPaymentResult(BeginPaymentOutcome.Replay, Snapshot(), ToModel(existing)));
        if (Order.Status != OrderStatus.PendingPayment)
            return Task.FromResult(new BeginPaymentResult(BeginPaymentOutcome.NotPayable, null, null));
        if (Exceptions.Count > 0)
            return Task.FromResult(new BeginPaymentResult(BeginPaymentOutcome.HeldForReview, null, null));
        if (Attempts.Any(a => a.Status == PaymentAttemptStatus.Initiated))
            return Task.FromResult(new BeginPaymentResult(BeginPaymentOutcome.InProgress, null, null));

        var row = new AttemptRow
        {
            Id = Guid.NewGuid(), OrderId = orderId, Number = Attempts.Count + 1, Key = idempotencyKey, Provider = provider, IsSimulated = isSimulated
        };
        Attempts.Add(row);
        return Task.FromResult(new BeginPaymentResult(BeginPaymentOutcome.Started, Snapshot(), ToModel(row)));
    }

    public Task SetProviderReferenceAsync(Guid attemptId, string providerReference, CancellationToken cancellationToken = default) =>
        SetProviderSessionAsync(attemptId, providerReference, null, cancellationToken);

    public Task SetProviderSessionAsync(Guid attemptId, string providerReference, string? checkoutUrl, CancellationToken cancellationToken = default)
    {
        var row = Attempts.Single(a => a.Id == attemptId);
        if (row.Reference is null)
        {
            row.Reference = providerReference;
            row.CheckoutUrl = row.Status == PaymentAttemptStatus.Initiated ? checkoutUrl : null; // same rule as the SQL
        }

        return Task.CompletedTask;
    }

    public Task<bool> HasPaymentExceptionAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(orderId == Order.OrderId && Exceptions.Count > 0);

    public Task CancelUncertainAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        Calls.Add("CancelUncertain");
        Seen.Add(("CancelUncertain", FinancialTransitionContext.Current));
        var row = Attempts.Single(a => a.Id == attemptId);
        if (row.Status == PaymentAttemptStatus.Initiated && row.Reference is null)
            row.Status = PaymentAttemptStatus.Cancelled;
        return Task.CompletedTask;
    }

    public Task CancelUnreferencedAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        Calls.Add("CancelUnreferenced");
        var row = Attempts.Single(a => a.Id == attemptId);
        if (row.Status == PaymentAttemptStatus.Initiated && row.Reference is null)
            row.Status = PaymentAttemptStatus.Cancelled;
        return Task.CompletedTask;
    }

    public Task<RecordAttemptOutcome> RecordAttemptOutcomeAsync(PaymentCallback callback, CancellationToken cancellationToken = default)
    {
        Seen.Add(("RecordAttemptOutcome", FinancialTransitionContext.Current));
        var row = Attempts.FirstOrDefault(a => a.Id == callback.AttemptId && a.OrderId == callback.OrderId);
        if (row is null || row.Reference is null || row.Reference != callback.ProviderReference)
            return Task.FromResult(RecordAttemptOutcome.NotFound);

        string? next = (row.Status, callback.Succeeded) switch
        {
            (PaymentAttemptStatus.Initiated, true) => PaymentAttemptStatus.Succeeded,
            (PaymentAttemptStatus.Initiated, false) => PaymentAttemptStatus.Failed,
            (PaymentAttemptStatus.Cancelled, true) => PaymentAttemptStatus.Succeeded,
            _ => null
        };
        if (next is null && row.Status == PaymentAttemptStatus.Failed && callback.Succeeded)
        {
            // The recorded Failed verdict stays; the contradiction becomes one exception per attempt.
            if (Exceptions.Any(e => e.AttemptId == row.Id))
                return Task.FromResult(RecordAttemptOutcome.LateSuccessAlreadyRecorded);
            Exceptions.Add((row.Id, callback.ProviderEventId));
            foreach (var other in Attempts.Where(x => x.Id != row.Id && x.Status == PaymentAttemptStatus.Initiated))
            {
                other.Status = PaymentAttemptStatus.Cancelled; // an attempt already in flight is withdrawn, like the SQL does
                other.CheckoutUrl = null;
            }

            return Task.FromResult(RecordAttemptOutcome.LateSuccessRecorded);
        }

        if (next is null)
        {
            var same = (row.Status == PaymentAttemptStatus.Succeeded && callback.Succeeded) ||
                       (row.Status is PaymentAttemptStatus.Failed or PaymentAttemptStatus.Cancelled && !callback.Succeeded);
            return Task.FromResult(same ? RecordAttemptOutcome.AlreadyRecordedSame : RecordAttemptOutcome.ConflictingOutcome);
        }

        row.Status = next;
        return Task.FromResult(RecordAttemptOutcome.Recorded);
    }

    public Task FinalizeAfterSaleAsync(Guid orderId, bool sold, CancellationToken cancellationToken = default)
    {
        Seen.Add(("FinalizeAfterSale", FinancialTransitionContext.Current));
        FinalizeCalls++;
        if (Order.Status is OrderStatus.Paid or OrderStatus.RefundRequired or OrderStatus.Refunded)
            return Task.CompletedTask; // idempotent: never leaves a final state

        Order = Order with { Status = sold ? OrderStatus.Paid : OrderStatus.RefundRequired };
        if (sold)
            PaidTransitions++;
        else
            RefundRequiredTransitions++;
        return Task.CompletedTask;
    }

    // Not used by the payment path.
    public Task<Order?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Order?> FindByReservationAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CreateOrderOutcome> TryCreateAsync(NewOrder order, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Order>> ListForCustomerAsync(Guid customerId, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CancelOrderResult> CancelAsync(Guid orderId, Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class InMemoryAttemptLookup(InMemoryOrderStore store) : IPaymentAttemptLookup
{
    public Task<AttemptRecord?> GetAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        if (store.LookupsFail)
            throw new InvalidOperationException("storage unavailable (simulated)");
        var row = store.Attempts.FirstOrDefault(a => a.Id == attemptId);
        return Task.FromResult(row is null
            ? null
            : new AttemptRecord(row.Id, row.OrderId, row.Status, row.Provider, row.Reference, DateTime.UtcNow, TimeSpan.Zero));
    }
}

internal sealed class ConfigurableGateway : IReservationCheckoutGateway
{
    public SaleResult Sale { get; set; } = SaleResult.Confirmed;
    public int ConfirmCalls { get; private set; }

    public Task<CheckoutStartResult> BeginAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SaleResult> ConfirmSaleAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        ConfirmCalls++;
        return Task.FromResult(Sale);
    }

    public Task ReleaseAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A provider whose behaviour is scripted per test; counts every call so tests can assert what was NOT called.</summary>
internal sealed class ScriptedProvider(string name, bool simulated = false, params string[] currencies) : IPaymentProvider
{
    public string Name => name;
    public bool IsSimulated => simulated;
    public bool IsAvailable { get; set; } = true;
    public bool Lookup { get; set; } = true;
    public bool Refunds { get; set; } = true;
    public bool Idempotent { get; set; } = true;
    public bool IdempotentInitiation => Idempotent;
    public int InitiateCalls { get; private set; }
    public int StatusCalls { get; private set; }
    public int RefundCalls { get; private set; }
    public int RefundStatusCalls { get; private set; }
    public Func<PaymentInitiation, ProviderSession> OnInitiate { get; set; } = i => new ProviderSession($"{name.ToLowerInvariant()}_ref_{i.AttemptId:N}", $"https://pay.example.test/{name}/{i.AttemptId}");
    public Func<PaymentStatusQuery, ProviderPaymentState> OnStatus { get; set; } = _ => ProviderPaymentState.Pending;
    public Func<ProviderRefundRequest, ProviderRefundResult> OnRefund { get; set; } = _ => new ProviderRefundResult(true, true, "re_1", null, false);
    public Func<RefundStatusQuery, ProviderRefundState> OnRefundStatus { get; set; } = _ => ProviderRefundState.NotFound;
    public PaymentStatusQuery? LastStatusQuery { get; private set; }
    public RefundStatusQuery? LastRefundStatusQuery { get; private set; }

    public bool SupportsCurrency(string currency) => currencies.Length == 0 || currencies.Contains(currency, StringComparer.OrdinalIgnoreCase);
    public bool SupportsPaymentLookup => Lookup;
    public bool SupportsRefunds => Refunds;

    public Task<ProviderSession> InitiateAsync(PaymentInitiation initiation, CancellationToken cancellationToken = default)
    {
        InitiateCalls++;
        return Task.FromResult(OnInitiate(initiation));
    }

    public Task<ProviderPaymentState> GetPaymentStatusAsync(Guid attemptId, string? providerReference, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("the query overload must be used");

    public Task<ProviderPaymentState> GetPaymentStatusAsync(PaymentStatusQuery query, CancellationToken cancellationToken = default)
    {
        StatusCalls++;
        LastStatusQuery = query;
        return Task.FromResult(OnStatus(query));
    }

    public Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken = default)
    {
        RefundCalls++;
        return Task.FromResult(OnRefund(request));
    }

    public Task<ProviderRefundState> GetRefundStatusAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("the query overload must be used");

    public Task<ProviderRefundState> GetRefundStatusAsync(RefundStatusQuery query, CancellationToken cancellationToken = default)
    {
        RefundStatusCalls++;
        LastRefundStatusQuery = query;
        return Task.FromResult(OnRefundStatus(query));
    }
}

/// <summary>An in-memory anomaly recorder with the same dedupe rule as the SQL (one per kind and dedupe key).</summary>
internal sealed class InMemoryAnomalyRecorder : IPaymentAnomalyRecorder
{
    public List<PaymentAnomalyRecord> Records { get; } = [];

    /// <summary>When set, recording fails (as a database outage would), which must stop the webhook from being acknowledged.</summary>
    public bool Fail { get; set; }

    public Task<bool> RecordAsync(PaymentAnomalyRecord record, CancellationToken cancellationToken = default)
    {
        if (Fail)
            throw new InvalidOperationException("storage unavailable");
        if (Records.Any(r => r.Kind == record.Kind && r.DedupeKey == record.DedupeKey))
            return Task.FromResult(false);
        Records.Add(record);
        return Task.FromResult(true);
    }
}

/// <summary>
/// A dynamic fake for a store interface: methods are answered by name from a table of delegates, every call is
/// recorded, and any method the test did not script fails loudly (so a test cannot pass by accident through an
/// unexpected call). Async methods may return the bare result value.
/// </summary>
public class DispatchFake : DispatchProxy
{
    public Dictionary<string, Func<object?[], object?>> Handlers { get; } = new();
    public List<string> Calls { get; } = [];

    public static T Create<T>(out DispatchFake fake) where T : class
    {
        var proxy = Create<T, DispatchFake>();
        fake = (DispatchFake)(object)proxy;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        Calls.Add(method.Name);
        if (!Handlers.TryGetValue(method.Name, out var handler))
            throw new InvalidOperationException($"Unexpected call to {method.Name}.");

        var value = handler(args ?? []);
        var returnType = method.ReturnType;
        if (returnType == typeof(Task))
            return Task.CompletedTask;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = returnType.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
        }

        return value;
    }
}
