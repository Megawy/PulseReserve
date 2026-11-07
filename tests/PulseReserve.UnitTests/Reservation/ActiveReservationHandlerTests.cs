using FluentAssertions;
using PulseReserve.Modules.Reservation.Features.Holds;
using PulseReserve.Modules.Reservation.Features.Holds.ManageReservation;
using PulseReserve.Modules.Reservation.Features.Inventory;
using PulseReserve.Modules.Reservation.Persistence;
using Xunit;

namespace PulseReserve.UnitTests.Reservation;

// "Find my live hold for this event" (a hold made in another browser, or after local state was cleared). The SQL, the expiry rule on the
// database clock and the ownership scoping are exercised against PostgreSQL in ReservationApiHttpTests (compiled, not executed).
public class ActiveReservationHandlerTests
{
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();

    private static ReservationResponse Live() =>
        new(Guid.NewGuid(), EventId, Customer, "Held", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10), 50m, "USD",
            [new ReservedSeatResponse(Guid.NewGuid(), 50m, "USD")]);

    [Fact]
    public async Task ALiveHold_IsReturnedAsTheServerReportsIt()
    {
        var hold = Live();
        var store = new ScriptedStore { Active = hold };

        var result = await new ReservationQueriesHandler(store).GetActiveAsync(new GetActiveReservationQuery(EventId, Customer));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(hold);
        store.Asked.Should().Equal((Customer, EventId));
    }

    [Fact]
    public async Task WhenThereIsNone_ItIsANotFoundThatNamesNoOtherCustomersReservation()
    {
        var store = new ScriptedStore();

        var result = await new ReservationQueriesHandler(store).GetActiveAsync(new GetActiveReservationQuery(EventId, Customer));

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Reservation.NoActiveHold");
        result.Error.Type.Should().Be(PulseReserve.SharedKernel.ErrorType.NotFound);
    }

    [Fact]
    public async Task TheQueryIsAlwaysScopedToTheCaller_NeverToAnIdentityTakenFromTheRequest()
    {
        var other = Guid.NewGuid();
        var store = new ScriptedStore { Active = Live() };

        await new ReservationQueriesHandler(store).GetActiveAsync(new GetActiveReservationQuery(EventId, other));

        store.Asked.Should().Equal((other, EventId)); // the handler passes exactly the customer it was given (the endpoint gives it the session's)
    }

    [Fact]
    public async Task AnEmptyCustomerIsRefusedWithoutTouchingTheStore_AndAnEmptyEventHasNoHold()
    {
        var store = new ScriptedStore { Active = Live() };
        var handler = new ReservationQueriesHandler(store);

        var noCustomer = await handler.GetActiveAsync(new GetActiveReservationQuery(EventId, Guid.Empty));
        var noEvent = await handler.GetActiveAsync(new GetActiveReservationQuery(Guid.Empty, Customer));

        noCustomer.Error.Code.Should().Be("Reservation.EmptyCustomerId");
        noEvent.Error.Code.Should().Be("Reservation.NoActiveHold");
        store.Asked.Should().BeEmpty();
    }

    private sealed class ScriptedStore : IReservationStore
    {
        public ReservationResponse? Active { get; set; }
        public List<(Guid Customer, Guid Event)> Asked { get; } = [];

        public Task<ReservationResponse?> FindActiveAsync(Guid customerId, Guid eventId, CancellationToken cancellationToken = default)
        {
            Asked.Add((customerId, eventId));
            return Task.FromResult(Active);
        }

        public Task<bool> InventoryExistsAsync(Guid eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SeedInventoryAsync(Guid eventId, IReadOnlyList<SellableSeat> seats, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReservationResponse?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<HoldAttempt> HoldAsync(HoldRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReservationResponse?> GetAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
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
