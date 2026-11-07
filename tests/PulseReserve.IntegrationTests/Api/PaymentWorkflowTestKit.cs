using System.Collections.Concurrent;
using PulseReserve.AppHost.Composition;
using PulseReserve.Modules.Payments.Features;
using Microsoft.Extensions.DependencyInjection;

namespace PulseReserve.IntegrationTests.Api;

public enum RefundMode
{
    /// <summary>Accept and complete immediately.</summary>
    Succeed,

    /// <summary>Accept; the provider finishes later (Pending).</summary>
    Pending,

    /// <summary>Definitive refusal, nothing will ever succeed for this submission.</summary>
    RejectPermanent,

    /// <summary>Definitive refusal, a new submission may work.</summary>
    RejectRetryable,

    /// <summary>Ambiguous: the request never reached the provider (nothing refunded).</summary>
    TimeoutBeforeProcessing,

    /// <summary>Ambiguous: the provider DID process the refund but the answer was lost.</summary>
    TimeoutAfterProcessing
}

/// <summary>
/// Test double for a real provider: it remembers refunds by idempotency key (like a real provider deduplicates),
/// counts how many times money actually moved per payment, and can be scripted to time out, refuse or stay pending.
/// </summary>
public sealed class ScriptedPaymentProvider : IPaymentProvider
{
    private readonly ConcurrentDictionary<string, string> _refunds = new(); // key -> provider refund id
    private readonly ConcurrentDictionary<string, int> _submissions = new();
    private readonly ConcurrentDictionary<Guid, int> _moneyMovedByRefund = new();

    public string Name => "Scripted";
    public bool IsSimulated => true;
    public bool IsAvailable => true;

    /// <summary>False models a provider whose refunds can only be made in its dashboard (such as Paymob).</summary>
    public bool SupportsRefunds { get; set; } = true;

    public RefundMode Mode { get; set; } = RefundMode.Succeed;
    public Func<Guid, string?, ProviderPaymentState> PaymentStatus { get; set; } = (_, reference) =>
        reference is null ? ProviderPaymentState.NotFound : ProviderPaymentState.Pending;

    public bool QueryRefundThrows { get; set; }
    public bool PaymentQueryThrows { get; set; }
    public ConcurrentDictionary<string, string> PendingRefundsFinished { get; } = new();

    public int RefundCalls(string key) => _submissions.GetValueOrDefault(key);

    public int MoneyMoved(Guid refundId) => _moneyMovedByRefund.GetValueOrDefault(refundId);

    public Task<ProviderSession> InitiateAsync(PaymentInitiation initiation, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderSession("scripted_" + Guid.NewGuid().ToString("N")));

    public Task<ProviderPaymentState> GetPaymentStatusAsync(Guid attemptId, string? providerReference, CancellationToken cancellationToken = default)
    {
        if (PaymentQueryThrows)
            throw new TimeoutException("provider timed out");
        return Task.FromResult(PaymentStatus(attemptId, providerReference));
    }

    public Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken = default)
    {
        _submissions.AddOrUpdate(request.IdempotencyKey, 1, (_, n) => n + 1);

        if (Mode == RefundMode.TimeoutBeforeProcessing)
            throw new TimeoutException("connection lost before the provider saw the request");

        if (_refunds.TryGetValue(request.IdempotencyKey, out var existing)) // provider-side idempotency
            return Task.FromResult(new ProviderRefundResult(true, !PendingRefundsFinished.IsEmpty || Mode != RefundMode.Pending, existing, null, false));

        switch (Mode)
        {
            case RefundMode.RejectPermanent:
                return Task.FromResult(new ProviderRefundResult(false, false, null, "account_closed", false));
            case RefundMode.RejectRetryable:
                return Task.FromResult(new ProviderRefundResult(false, false, null, "temporarily_unavailable", true));
        }

        var id = "scripted_refund_" + Guid.NewGuid().ToString("N")[..12];
        _refunds[request.IdempotencyKey] = id;
        _moneyMovedByRefund.AddOrUpdate(request.RefundId, 1, (_, n) => n + 1);

        if (Mode == RefundMode.TimeoutAfterProcessing)
            throw new TimeoutException("the provider processed the refund but the answer was lost");

        return Task.FromResult(new ProviderRefundResult(true, Mode == RefundMode.Succeed, id, null, false));
    }

    public Task<ProviderRefundState> GetRefundStatusAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (QueryRefundThrows)
            throw new TimeoutException("provider timed out");
        if (!_refunds.ContainsKey(idempotencyKey))
            return Task.FromResult(ProviderRefundState.NotFound);
        return Task.FromResult(PendingRefundsFinished.ContainsKey(idempotencyKey) || Mode != RefundMode.Pending
            ? ProviderRefundState.Succeeded
            : ProviderRefundState.Pending);
    }
}

public sealed class RecordingNotificationSender : INotificationSender
{
    private readonly ConcurrentQueue<NotificationRequest> _sent = new();

    public string Channel => "Recording";
    public bool IsAvailable => true;
    public bool Fail { get; set; }
    public IReadOnlyCollection<NotificationRequest> Sent => _sent;

    public Task SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        if (Fail)
            throw new InvalidOperationException("mail server unreachable");
        _sent.Enqueue(request);
        return Task.CompletedTask;
    }
}

/// <summary>Throws for chosen reservations so a poison item can be mixed with healthy work.</summary>
public sealed class PoisonableGateway(IReservationCheckoutGateway inner, ConcurrentDictionary<Guid, bool> poison) : IReservationCheckoutGateway
{
    public Task<CheckoutStartResult> BeginAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
        inner.BeginAsync(reservationId, customerId, cancellationToken);

    public Task<SaleResult> ConfirmSaleAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
        poison.ContainsKey(reservationId) ? throw new InvalidOperationException("poison") : inner.ConfirmSaleAsync(reservationId, cancellationToken);

    public Task ReleaseAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
        inner.ReleaseAsync(reservationId, customerId, cancellationToken);

    public static void Install(IServiceCollection services, ConcurrentDictionary<Guid, bool> poison) =>
        services.AddScoped<IReservationCheckoutGateway>(sp =>
            new PoisonableGateway(ActivatorUtilities.CreateInstance<ReservationCheckoutAdapter>(sp), poison));
}
