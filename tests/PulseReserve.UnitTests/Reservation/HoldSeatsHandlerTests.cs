using FluentAssertions;
using PulseReserve.Modules.Reservation;
using PulseReserve.Modules.Reservation.Features.Holds;
using PulseReserve.Modules.Reservation.Features.Holds.HoldSeats;
using PulseReserve.Modules.Reservation.Features.Inventory;
using PulseReserve.Modules.Reservation.Persistence;
using Xunit;

namespace PulseReserve.UnitTests.Reservation;

// Handler-level rules that need no database. Concurrency and integrity are covered against PostgreSQL in
// ReservationApiHttpTests.
public class HoldSeatsHandlerTests
{
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly Guid SeatA = Guid.NewGuid();
    private static readonly Guid SeatB = Guid.NewGuid();

    private readonly FakeCatalog _catalog = new();
    private readonly FakeStore _store = new();

    public static TheoryData<HoldSeatsCommand, string> InvalidCommands => new()
    {
        { new HoldSeatsCommand(EventId, Guid.Empty, [SeatA], "k"), "Reservation.EmptyCustomerId" },
        { new HoldSeatsCommand(EventId, Customer, [SeatA], null), "Reservation.MissingIdempotencyKey" },
        { new HoldSeatsCommand(EventId, Customer, [SeatA], "   "), "Reservation.MissingIdempotencyKey" },
        { new HoldSeatsCommand(EventId, Customer, [SeatA], new string('k', 101)), "Reservation.IdempotencyKeyTooLong" },
        { new HoldSeatsCommand(EventId, Customer, [], "k"), "Reservation.NoSeats" },
        { new HoldSeatsCommand(EventId, Customer, Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToList(), "k"), "Reservation.TooManySeats" },
        { new HoldSeatsCommand(EventId, Customer, [SeatA, SeatA], "k"), "Reservation.InvalidSeatIds" },
        { new HoldSeatsCommand(EventId, Customer, [Guid.Empty], "k"), "Reservation.InvalidSeatIds" }
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public async Task InvalidCommands_AreRejected_BeforeAnyStoreOrCatalogCall(HoldSeatsCommand command, string expectedCode)
    {
        var attempt = await Handler().HandleAsync(command);

        attempt.Error!.Code.Should().Be(expectedCode);
        _store.Calls.Should().BeEmpty();
        _catalog.Calls.Should().Be(0);
    }

    [Fact]
    public async Task EventNotOnSale_IsAConflict_AndNothingIsHeld()
    {
        _catalog.Status = new EventSalesStatus(EventId, "Published", IsOnSale: false);

        var attempt = await Handler().HandleAsync(new HoldSeatsCommand(EventId, Customer, [SeatA], "k"));

        attempt.Error!.Code.Should().Be("Reservation.SalesNotOpen");
        _store.Calls.Should().NotContain("Hold");
    }

    [Fact]
    public async Task UnknownEvent_IsNotFound()
    {
        _catalog.Status = null;

        var attempt = await Handler().HandleAsync(new HoldSeatsCommand(EventId, Customer, [SeatA], "k"));

        attempt.Error!.Code.Should().Be("Event.NotFound");
    }

    [Fact]
    public async Task ExistingKeyWithSameRequest_IsReplayed_WithoutCheckingSalesStatus()
    {
        _store.Existing = Reservation([SeatB, SeatA]);

        var attempt = await Handler().HandleAsync(new HoldSeatsCommand(EventId, Customer, [SeatA, SeatB], " k "));

        attempt.IsReplay.Should().BeTrue();
        attempt.Reservation.Should().BeSameAs(_store.Existing);
        _catalog.Calls.Should().Be(0);
        _store.LastKey.Should().Be("k");
    }

    [Fact]
    public async Task ExistingKeyWithDifferentSeats_IsRejected()
    {
        _store.Existing = Reservation([SeatA]);

        var attempt = await Handler().HandleAsync(new HoldSeatsCommand(EventId, Customer, [SeatB], "k"));

        attempt.Error!.Code.Should().Be("Reservation.IdempotencyKeyReused");
        _store.Calls.Should().NotContain("Hold");
    }

    [Fact]
    public async Task OnSaleEvent_IsSeededOnce_AndHeldWithConfiguredDuration()
    {
        var options = new ReservationOptions { HoldDuration = TimeSpan.FromMinutes(3) };

        await Handler(options).HandleAsync(new HoldSeatsCommand(EventId, Customer, [SeatA], "k"));

        _store.Calls.Should().Equal("FindByKey", "InventoryExists", "Seed", "Hold");
        _store.LastHold!.HoldDuration.Should().Be(TimeSpan.FromMinutes(3));
        _store.LastHold.ReservationId.Should().NotBeEmpty();
    }

    private HoldSeatsHandler Handler(ReservationOptions? options = null) =>
        new(new EventInventoryProvisioner(_catalog, _store), _store, options ?? new ReservationOptions());

    private static ReservationResponse Reservation(IReadOnlyList<Guid> seats) =>
        new(Guid.NewGuid(), EventId, Customer, "Held", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10), 0m, "USD",
            seats.Select(s => new ReservedSeatResponse(s, 0m, "USD")).ToList());

    private sealed class FakeCatalog : IEventSalesCatalog
    {
        public EventSalesStatus? Status { get; set; } = new(EventId, "OnSale", IsOnSale: true);
        public int Calls { get; private set; }

        public Task<EventSalesStatus?> GetSalesStatusAsync(Guid eventId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Status);
        }

        public Task<IReadOnlyList<SellableSeat>> GetSellableSeatsAsync(Guid eventId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<SellableSeat>>([new SellableSeat(SeatA, 10m, "USD")]);
        }
    }

    private sealed class FakeStore : IReservationStore
    {
        public List<string> Calls { get; } = [];
        public ReservationResponse? Existing { get; set; }
        public string? LastKey { get; private set; }
        public HoldRequest? LastHold { get; private set; }

        public Task<bool> InventoryExistsAsync(Guid eventId, CancellationToken cancellationToken = default)
        {
            Calls.Add("InventoryExists");
            return Task.FromResult(false);
        }

        public Task SeedInventoryAsync(Guid eventId, IReadOnlyList<SellableSeat> seats, CancellationToken cancellationToken = default)
        {
            Calls.Add("Seed");
            return Task.CompletedTask;
        }

        public Task<ReservationResponse?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            Calls.Add("FindByKey");
            LastKey = idempotencyKey;
            return Task.FromResult(Existing);
        }

        public Task<HoldAttempt> HoldAsync(HoldRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("Hold");
            LastHold = request;
            return Task.FromResult(HoldAttempt.Created(Reservation(request.SeatIds)));
        }

        public Task<ReservationResponse?> GetAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReservationResponse?> FindActiveAsync(Guid customerId, Guid eventId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReservationResponse?> ReleaseAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SeatAvailability>> GetAvailabilityAsync(Guid eventId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BeginCheckoutResult> BeginCheckoutAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConfirmSaleResult> ConfirmSaleAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReservationResponse?> ReleaseCheckoutAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
