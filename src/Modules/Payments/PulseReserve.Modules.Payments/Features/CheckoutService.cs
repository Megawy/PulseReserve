using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Payments.Features;

public sealed record CheckoutResult(Order Order, bool IsReplay);

/// <summary>
/// Creates and manages orders. Order totals are NEVER taken from the client: the lines and prices come from the
/// reservation's server-side snapshot returned by the gateway.
/// <para>
/// Creation is a two-step saga across two modules, made safe by idempotent steps: (1) the Reservation module
/// atomically moves Held -> Checkout (or resumes a Checkout), (2) this module inserts the order, with the database
/// deciding uniqueness. A crash between the steps leaves a Checkout reservation without an order; repeating the same
/// request resumes at step 2, and if nobody does the hold simply expires at its original time.
/// </para>
/// </summary>
public sealed class CheckoutService(IOrderStore store, IReservationCheckoutGateway reservations)
{
    public async Task<Result<CheckoutResult>> CreateOrderAsync(
        Guid customerId, Guid reservationId, string? idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (reservationId == Guid.Empty)
            return Result.Failure<CheckoutResult>(PaymentsErrors.InvalidReservation);
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Result.Failure<CheckoutResult>(PaymentsErrors.MissingIdempotencyKey);
        var key = idempotencyKey.Trim();
        if (key.Length > OrderPolicy.MaxIdempotencyKeyLength)
            return Result.Failure<CheckoutResult>(PaymentsErrors.IdempotencyKeyTooLong);

        using var scope = FinancialTransitionContext.Begin(TransitionSource.Customer, FactBasis.ApplicationDecision, actorUserId: customerId);

        // A retry of an already created request returns the original order, whatever has happened to it since.
        var byKey = await store.FindByIdempotencyKeyAsync(customerId, key, cancellationToken);
        if (byKey is not null)
            return byKey.ReservationId == reservationId
                ? Result.Success(new CheckoutResult(byKey, true))
                : Result.Failure<CheckoutResult>(PaymentsErrors.IdempotencyKeyReused);

        // Only one payable order may ever exist per reservation, whatever key the request carries.
        var existing = await store.FindByReservationAsync(reservationId, customerId, cancellationToken);
        if (existing is not null)
            return ResolveExisting(existing);

        var start = await reservations.BeginAsync(reservationId, customerId, cancellationToken);
        if (start.Status == CheckoutStart.NotFound)
            return Result.Failure<CheckoutResult>(PaymentsErrors.ReservationNotFound);
        if (start.Status == CheckoutStart.NotActive || start.Snapshot is null)
            return Result.Failure<CheckoutResult>(PaymentsErrors.ReservationNotActive);

        var snapshot = start.Snapshot;
        var created = await store.TryCreateAsync(new NewOrder(Guid.NewGuid(), customerId, key, snapshot), cancellationToken);
        if (created == CreateOrderOutcome.Created)
        {
            var order = await store.FindByIdempotencyKeyAsync(customerId, key, cancellationToken)
                ?? throw new InvalidOperationException("Order was committed but could not be read back.");
            return Result.Success(new CheckoutResult(order, false));
        }

        // A concurrent request won the race: answer exactly like a sequential retry would.
        var winner = await store.FindByIdempotencyKeyAsync(customerId, key, cancellationToken);
        if (winner is not null)
            return winner.ReservationId == reservationId
                ? Result.Success(new CheckoutResult(winner, true))
                : Result.Failure<CheckoutResult>(PaymentsErrors.IdempotencyKeyReused);

        var byReservation = await store.FindByReservationAsync(reservationId, customerId, cancellationToken);
        return byReservation is not null
            ? ResolveExisting(byReservation)
            : Result.Failure<CheckoutResult>(PaymentsErrors.ReservationConsumed);
    }

    public Task<Order?> GetAsync(Guid orderId, Guid customerId, CancellationToken cancellationToken = default) =>
        store.GetAsync(orderId, customerId, cancellationToken);

    public Task<IReadOnlyList<Order>> ListAsync(Guid customerId, int limit, CancellationToken cancellationToken = default) =>
        store.ListForCustomerAsync(customerId, limit, cancellationToken);

    public Task<Order?> GetByReservationAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
        store.FindByReservationAsync(reservationId, customerId, cancellationToken);

    public async Task<Result<Order>> CancelAsync(Guid orderId, Guid customerId, CancellationToken cancellationToken = default)
    {
        using var scope = FinancialTransitionContext.Begin(TransitionSource.Customer, FactBasis.ApplicationDecision, actorUserId: customerId, reason: TransitionReason.CustomerCancelled);
        var result = await store.CancelAsync(orderId, customerId, cancellationToken);
        if (result.Outcome == CancelOrderOutcome.NotFound)
            return Result.Failure<Order>(PaymentsErrors.OrderNotFound);
        if (result.Outcome == CancelOrderOutcome.NotCancellable)
            return Result.Failure<Order>(PaymentsErrors.OrderNotCancellable);

        // Idempotent: also repairs a previous cancel that stopped before the seats were returned.
        await reservations.ReleaseAsync(result.Order!.ReservationId, customerId, cancellationToken);
        return Result.Success(result.Order);
    }

    private static Result<CheckoutResult> ResolveExisting(Order existing) =>
        existing.Status == OrderStatus.PendingPayment
            ? Result.Success(new CheckoutResult(existing, true))
            : Result.Failure<CheckoutResult>(PaymentsErrors.ReservationConsumed);
}
