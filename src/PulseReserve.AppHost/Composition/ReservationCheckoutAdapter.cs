using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Reservation.Features.Checkout;
using PulseReserve.Modules.Reservation.Persistence;

namespace PulseReserve.AppHost.Composition;

/// <summary>
/// Composition-root adapter that answers Payments' <see cref="IReservationCheckoutGateway"/> port with the Reservation
/// module's atomic checkout transitions. Neither module references the other; the row locks, the database-clock expiry
/// decision and the seat transitions all stay inside Reservation.
/// </summary>
public sealed class ReservationCheckoutAdapter(ReservationCheckoutService reservations) : IReservationCheckoutGateway
{
    public async Task<CheckoutStartResult> BeginAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default)
    {
        var result = await reservations.BeginAsync(reservationId, customerId, cancellationToken);
        switch (result.Outcome)
        {
            case BeginCheckoutOutcome.NotFound:
                return new CheckoutStartResult(CheckoutStart.NotFound, null);
            case BeginCheckoutOutcome.NotActive:
                return new CheckoutStartResult(CheckoutStart.NotActive, null);
        }

        var reservation = result.Reservation
            ?? throw new InvalidOperationException("A reservation in Checkout could not be read back.");
        var snapshot = new ReservationSnapshot(
            reservation.ReservationId,
            reservation.EventId,
            reservation.Currency,
            reservation.ExpiresAtUtc,
            reservation.Seats.Select(s => new ReservationLine(s.SeatId, s.PriceAmount)).ToList());
        return new CheckoutStartResult(CheckoutStart.Ready, snapshot);
    }

    public async Task<SaleResult> ConfirmSaleAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        var result = await reservations.ConfirmSaleAsync(reservationId, cancellationToken);
        return result.Outcome switch
        {
            ConfirmSaleOutcome.Confirmed or ConfirmSaleOutcome.AlreadyConfirmed => SaleResult.Confirmed,
            ConfirmSaleOutcome.Expired => SaleResult.Expired,
            _ => SaleResult.NotConfirmable
        };
    }

    public async Task ReleaseAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
        await reservations.ReleaseCheckoutAsync(reservationId, customerId, cancellationToken);
}
