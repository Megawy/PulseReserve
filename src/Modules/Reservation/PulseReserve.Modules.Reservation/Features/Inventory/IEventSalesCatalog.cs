namespace PulseReserve.Modules.Reservation.Features.Inventory;

/// <summary>
/// Port through which Reservation reads the sellable state of an event. Reservation owns this contract and has no
/// dependency on Catalog; the composition root supplies an adapter over Catalog persistence.
/// </summary>
public interface IEventSalesCatalog
{
    /// <summary>Returns the event's sales status, or <c>null</c> when the event does not exist.</summary>
    Task<EventSalesStatus?> GetSalesStatusAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every seat with its event-specific price. Only valid while the event is on sale, when the seating
    /// layout is locked and pricing is frozen, so the result is an immutable snapshot (ADR-006).
    /// </summary>
    Task<IReadOnlyList<SellableSeat>> GetSellableSeatsAsync(Guid eventId, CancellationToken cancellationToken = default);
}

public sealed record EventSalesStatus(Guid EventId, string Status, bool IsOnSale);

public sealed record SellableSeat(Guid SeatId, decimal PriceAmount, string Currency);
