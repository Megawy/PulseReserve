using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Persistence;
using PulseReserve.Modules.Payments.Providers;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

// Orchestration rules of CheckoutService and PaymentService with in-memory fakes. Atomicity, locking and uniqueness
// are database properties and are covered by the PostgreSQL integration tests.
public class CheckoutAndPaymentServiceTests
{
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateOrder_RequiresAnIdempotencyKey_BeforeTouchingAnything(string? key)
    {
        var gateway = new FakeGateway();
        var result = await new CheckoutService(new FakeStore(), gateway).CreateOrderAsync(Customer, ReservationId, key);

        result.Error.Should().Be(PaymentsErrors.MissingIdempotencyKey);
        gateway.BeginCalls.Should().Be(0);
    }

    [Fact]
    public async Task CreateOrder_RejectsAnEmptyReservationId_AndAnOverlongKey()
    {
        var service = new CheckoutService(new FakeStore(), new FakeGateway());

        (await service.CreateOrderAsync(Customer, Guid.Empty, "k")).Error.Should().Be(PaymentsErrors.InvalidReservation);
        (await service.CreateOrderAsync(Customer, ReservationId, new string('k', 101))).Error.Should().Be(PaymentsErrors.IdempotencyKeyTooLong);
    }

    [Fact]
    public async Task CreateOrder_MapsGatewayRefusals_AndNeverStoresAnOrderForThem()
    {
        var store = new FakeStore();
        var gateway = new FakeGateway { Start = new CheckoutStartResult(CheckoutStart.NotActive, null) };
        var service = new CheckoutService(store, gateway);

        (await service.CreateOrderAsync(Customer, ReservationId, "k")).Error.Should().Be(PaymentsErrors.ReservationNotActive);
        gateway.Start = new CheckoutStartResult(CheckoutStart.NotFound, null);
        (await service.CreateOrderAsync(Customer, ReservationId, "k")).Error.Should().Be(PaymentsErrors.ReservationNotFound);
        store.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateOrder_TakesLinesAndTotalFromTheServerSnapshot_UsingDecimalMath()
    {
        var store = new FakeStore();
        var seatA = Guid.NewGuid();
        var seatB = Guid.NewGuid();
        var gateway = new FakeGateway
        {
            Start = new CheckoutStartResult(CheckoutStart.Ready,
                new ReservationSnapshot(ReservationId, Guid.NewGuid(), "USD", DateTime.UtcNow.AddMinutes(5),
                    [new ReservationLine(seatA, 0.1m), new ReservationLine(seatB, 0.2m)]))
        };

        var result = await new CheckoutService(store, gateway).CreateOrderAsync(Customer, ReservationId, "k");

        result.IsSuccess.Should().BeTrue();
        store.Created.Should().ContainSingle();
        store.Created[0].Snapshot.Lines.Sum(l => l.UnitPrice).Should().Be(0.3m, "decimal arithmetic, not 0.30000000000000004");
        result.Value.Order.TotalAmount.Should().Be(0.3m);
    }

    [Fact]
    public async Task CreateOrder_ReusingAKeyForAnotherReservation_IsAConflict()
    {
        var store = new FakeStore { ByKey = FakeStore.SomeOrder(Guid.NewGuid()) };
        var result = await new CheckoutService(store, new FakeGateway()).CreateOrderAsync(Customer, ReservationId, "k");

        result.Error.Should().Be(PaymentsErrors.IdempotencyKeyReused);
    }

    [Fact]
    public async Task InitiatePayment_WithoutAProvider_IsUnavailable_AndCreatesNoAttempt()
    {
        var store = new FakeStore();
        var service = new PaymentService(store, new UnavailablePaymentProvider(), new FakeGateway());

        var result = await service.InitiateAsync(Customer, Guid.NewGuid(), "k");

        result.Error.Should().Be(PaymentsErrors.ProviderUnavailable);
        store.BeginPaymentCalls.Should().Be(0);
    }

    [Fact]
    public async Task Complete_WithAMismatchedCallback_FailsWithoutSellingAnything()
    {
        var gateway = new FakeGateway();
        var store = new FakeStore { Recorded = RecordAttemptOutcome.NotFound };
        var service = new PaymentService(store, new SimulatedPaymentProvider(), gateway);

        var result = await service.CompleteAsync(new PaymentCallback(Guid.NewGuid(), Guid.NewGuid(), "sim_x", true, null));

        result.Error.Should().Be(PaymentsErrors.AttemptNotFound);
        gateway.ConfirmCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(SaleResult.Confirmed, true)]
    [InlineData(SaleResult.Expired, false)]
    [InlineData(SaleResult.NotConfirmable, false)]
    public async Task Complete_Success_FinalisesTheOrderAsPaidOnlyIfTheSaleWasConfirmed(SaleResult sale, bool paid)
    {
        var store = new FakeStore { Recorded = RecordAttemptOutcome.Recorded, ByKey = FakeStore.SomeOrder(ReservationId) };
        var gateway = new FakeGateway { Sale = sale };
        var service = new PaymentService(store, new SimulatedPaymentProvider(), gateway);

        var result = await service.CompleteAsync(new PaymentCallback(Guid.NewGuid(), store.ByKey!.OrderId, "sim_x", true, null));

        result.IsSuccess.Should().BeTrue();
        store.Finalized.Should().Be(paid);
        gateway.ConfirmCalls.Should().Be(1);
    }

    [Fact]
    public async Task Complete_Failure_NeverAsksTheReservationToSellSeats()
    {
        var store = new FakeStore { Recorded = RecordAttemptOutcome.Recorded, ByKey = FakeStore.SomeOrder(ReservationId) };
        var gateway = new FakeGateway();
        var service = new PaymentService(store, new SimulatedPaymentProvider(), gateway);

        await service.CompleteAsync(new PaymentCallback(Guid.NewGuid(), store.ByKey!.OrderId, "sim_x", false, "declined"));

        gateway.ConfirmCalls.Should().Be(0);
        store.Finalized.Should().BeNull();
    }

    [Fact]
    public async Task Simulator_IssuesUnguessableDistinctReferences_AndIsLabelledSimulated()
    {
        var provider = new SimulatedPaymentProvider();
        var a = await provider.InitiateAsync(new PaymentInitiation(Guid.NewGuid(), Guid.NewGuid(), 10m, "USD"));
        var b = await provider.InitiateAsync(new PaymentInitiation(Guid.NewGuid(), Guid.NewGuid(), 10m, "USD"));

        provider.IsSimulated.Should().BeTrue();
        a.ProviderReference.Should().StartWith("sim_").And.NotBe(b.ProviderReference);
        a.ProviderReference.Length.Should().BeGreaterThan(40);
    }

    private sealed class FakeGateway : IReservationCheckoutGateway
    {
        public int BeginCalls { get; private set; }
        public int ConfirmCalls { get; private set; }
        public CheckoutStartResult Start { get; set; } = new(CheckoutStart.NotFound, null);
        public SaleResult Sale { get; set; } = SaleResult.Confirmed;

        public Task<CheckoutStartResult> BeginAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default)
        {
            BeginCalls++;
            return Task.FromResult(Start);
        }

        public Task<SaleResult> ConfirmSaleAsync(Guid reservationId, CancellationToken cancellationToken = default)
        {
            ConfirmCalls++;
            return Task.FromResult(Sale);
        }

        public Task ReleaseAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeStore : IOrderStore
    {
        public List<NewOrder> Created { get; } = [];
        public Order? ByKey { get; set; }
        public RecordAttemptOutcome Recorded { get; set; }
        public bool? Finalized { get; private set; }
        public int BeginPaymentCalls { get; private set; }

        public static Order SomeOrder(Guid reservationId) =>
            new(Guid.NewGuid(), reservationId, Guid.NewGuid(), Customer, OrderStatus.PendingPayment, "USD", 10m,
                DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5), [], null);

        public Task<Order?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(ByKey);

        public Task<Order?> FindByReservationAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(null);

        public Task<CreateOrderOutcome> TryCreateAsync(NewOrder order, CancellationToken cancellationToken = default)
        {
            Created.Add(order);
            ByKey = new Order(order.OrderId, order.Snapshot.ReservationId, order.Snapshot.EventId, order.CustomerId,
                OrderStatus.PendingPayment, order.Snapshot.Currency, order.Snapshot.Lines.Sum(l => l.UnitPrice),
                DateTime.UtcNow, order.Snapshot.ExpiresAtUtc, [], null);
            return Task.FromResult(CreateOrderOutcome.Created);
        }

        public Task<IReadOnlyList<Order>> ListForCustomerAsync(Guid customerId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>([]);

        public Task<Order?> GetAsync(Guid orderId, Guid? customerId, CancellationToken cancellationToken = default) => Task.FromResult(ByKey);

        public Task<CancelOrderResult> CancelAsync(Guid orderId, Guid customerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BeginPaymentResult> BeginPaymentAsync(Guid orderId, Guid customerId, string idempotencyKey, string provider, bool isSimulated, CancellationToken cancellationToken = default)
        {
            BeginPaymentCalls++;
            throw new NotSupportedException();
        }

        public Task SetProviderReferenceAsync(Guid attemptId, string providerReference, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RecordAttemptOutcome> RecordAttemptOutcomeAsync(PaymentCallback callback, CancellationToken cancellationToken = default) =>
            Task.FromResult(Recorded);

        public Task FinalizeAfterSaleAsync(Guid orderId, bool sold, CancellationToken cancellationToken = default)
        {
            Finalized = sold;
            return Task.CompletedTask;
        }
    }
}
