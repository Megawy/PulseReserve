using PulseReserve.Modules.Reservation.Features.Holds;
using PulseReserve.Modules.Reservation.Persistence;

namespace PulseReserve.Modules.Reservation.Features.Checkout;

/// <summary>
/// The Reservation module's public checkout surface, consumed by the composition root (never by another module's
/// domain). It only forwards to the store, where the atomic, row-locked transitions live.
/// </summary>
public sealed class ReservationCheckoutService(IReservationStore store)
{
    public Task<BeginCheckoutResult> BeginAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
        store.BeginCheckoutAsync(reservationId, customerId, cancellationToken);

    public Task<ConfirmSaleResult> ConfirmSaleAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
        store.ConfirmSaleAsync(reservationId, cancellationToken);

    public Task<ReservationResponse?> ReleaseCheckoutAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
        store.ReleaseCheckoutAsync(reservationId, customerId, cancellationToken);
}
