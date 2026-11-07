using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Payments.Features;

public sealed record PaymentStartResult(Order Order, PaymentAttempt Attempt, bool IsReplay);

/// <summary>A provider the customer may choose. Only names and flags: no configuration ever leaves the server.</summary>
public sealed record PaymentOption(string Name, bool IsSimulated);

/// <summary>
/// The payment methods offered for ONE order. <c>Eligible</c> is false with a customer-safe <c>Reason</c>
/// (<see cref="PaymentIneligibility"/>) when the order cannot be paid; then <c>Providers</c> is empty, so no method is
/// ever offered that would be refused. Provider capabilities are a separate, administrator-side concern.
/// </summary>
public sealed record PaymentOptions(string Currency, IReadOnlyList<PaymentOption> Providers, bool Eligible = true, string? Reason = null);

/// <summary>
/// Payment attempts for an order. Initiation is idempotent per (order, key); completion is idempotent per attempt and
/// only trusts a verdict whose order ID, attempt ID and provider reference all match what was stored. The payment
/// verdict never decides seat ownership: the Reservation module's atomic sale confirmation does, so a late success
/// after expiry ends as RefundRequired instead of confirming sold-out seats.
/// <para>
/// Provider choice is server-controlled: the customer may only NAME one of the enabled providers (or none when exactly
/// one is available). The provider is fixed on the attempt when it is created and never changes; a replay that names
/// a different provider is a conflict, and an ambiguous provider failure leaves the attempt in place for an idempotent
/// retry instead of starting a second charge elsewhere.
/// </para>
/// </summary>
public sealed class PaymentService(IOrderStore store, IPaymentProviderRegistry providers, IReservationCheckoutGateway reservations)
{
    /// <summary>Single-provider convenience (tests and simple hosts).</summary>
    public PaymentService(IOrderStore store, IPaymentProvider provider, IReservationCheckoutGateway reservations)
        : this(store, PaymentProviderRegistry.Single(provider), reservations)
    {
    }

    /// <summary>The providers a customer may pick for this order right now: enabled, configured and able to charge the order currency.</summary>
    public async Task<Result<PaymentOptions>> OptionsAsync(Guid customerId, Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await store.GetAsync(orderId, customerId, cancellationToken);
        if (order is null)
            return Result.Failure<PaymentOptions>(PaymentsErrors.OrderNotFound);
        if (order.Status != OrderStatus.PendingPayment || order.PaymentDeadlineUtc <= DateTime.UtcNow)
            return Result.Success(new PaymentOptions(order.Currency, [], false, PaymentIneligibility.OrderNotPayable));
        if (await store.HasPaymentExceptionAsync(orderId, cancellationToken))
            return Result.Success(new PaymentOptions(order.Currency, [], false, PaymentIneligibility.OrderHeldForReview));

        var eligible = providers.Available.Where(p => p.SupportsCurrency(order.Currency)).Select(p => new PaymentOption(p.Name, p.IsSimulated)).ToList();
        return eligible.Count == 0
            ? Result.Success(new PaymentOptions(order.Currency, eligible, false, PaymentIneligibility.NoPaymentMethod))
            : Result.Success(new PaymentOptions(order.Currency, eligible));
    }

    public async Task<Result<PaymentStartResult>> InitiateAsync(
        Guid customerId, Guid orderId, string? idempotencyKey, string? providerName = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Result.Failure<PaymentStartResult>(PaymentsErrors.MissingIdempotencyKey);
        var key = idempotencyKey.Trim();
        if (key.Length > OrderPolicy.MaxIdempotencyKeyLength)
            return Result.Failure<PaymentStartResult>(PaymentsErrors.IdempotencyKeyTooLong);
        if (providers.Available.Count == 0)
            return Result.Failure<PaymentStartResult>(PaymentsErrors.ProviderUnavailable);

        using var scope = FinancialTransitionContext.Begin(TransitionSource.Customer, FactBasis.ApplicationDecision, actorUserId: customerId);

        // The order's currency (server-side) decides eligibility; nothing about the charge comes from the client.
        var existing = await store.GetAsync(orderId, customerId, cancellationToken);
        if (existing is null)
            return Result.Failure<PaymentStartResult>(PaymentsErrors.OrderNotFound);

        // A key that already produced an attempt always answers with THAT attempt (and its provider), whatever the
        // available set looks like now; naming a different provider on such a replay is refused, never honoured.
        var replayed = await store.FindAttemptByKeyAsync(orderId, key, cancellationToken);
        IPaymentProvider provider;
        if (replayed is not null)
        {
            if (!string.IsNullOrWhiteSpace(providerName) && !string.Equals(replayed.Provider, providerName.Trim(), StringComparison.OrdinalIgnoreCase))
                return Result.Failure<PaymentStartResult>(PaymentsErrors.ProviderMismatch);
            var original = providers.Find(replayed.Provider);
            if (original is null || !original.IsAvailable)
                return Result.Failure<PaymentStartResult>(PaymentsErrors.ProviderUnavailable);
            provider = original;
        }
        else
        {
            var selected = providers.Select(providerName, existing.Currency);
            if (selected.IsFailure)
                return Result.Failure<PaymentStartResult>(selected.Error);
            provider = selected.Value;
        }

        var begin = await store.BeginPaymentAsync(orderId, customerId, key, provider.Name, provider.IsSimulated, cancellationToken);
        switch (begin.Outcome)
        {
            case BeginPaymentOutcome.OrderNotFound:
                return Result.Failure<PaymentStartResult>(PaymentsErrors.OrderNotFound);
            case BeginPaymentOutcome.NotPayable:
                return Result.Failure<PaymentStartResult>(PaymentsErrors.OrderNotPayable);
            case BeginPaymentOutcome.InProgress:
                return Result.Failure<PaymentStartResult>(PaymentsErrors.AttemptInProgress);
            case BeginPaymentOutcome.HeldForReview:
                return Result.Failure<PaymentStartResult>(PaymentsErrors.OrderHeldForReview);
        }

        var order = begin.Order!;
        var attempt = begin.Attempt!;

        // The attempt row (with its provider) is durable now. A replay uses the provider stored on it, never the
        // currently requested one.
        if (!string.Equals(attempt.Provider, provider.Name, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<PaymentStartResult>(PaymentsErrors.ProviderMismatch);

        // Outside any database transaction: the provider call may be slow. It is idempotent per attempt, so a retry
        // after a crash between the insert and this point simply completes the missing step.
        if (attempt.ProviderReference is null)
        {
            // A provider that cannot deduplicate initiation is asked AT MOST ONCE per attempt. A replay of an attempt whose answer
            // was never stored is therefore not sent to the provider again: if the first call is still running the attempt is in
            // progress (a crash is cleaned up by reconciliation after the stale-attempt age); if it was closed the customer starts
            // again with a new attempt. This application does not claim that a repeat could not have created a second session.
            if (!provider.IdempotentInitiation && begin.Outcome == BeginPaymentOutcome.Replay)
            {
                return Result.Failure<PaymentStartResult>(attempt.Status == PaymentAttemptStatus.Initiated
                    ? PaymentsErrors.AttemptInProgress
                    : PaymentsErrors.InitiationUncertain);
            }

            ProviderSession session;
            try
            {
                session = await provider.InitiateAsync(
                    new PaymentInitiation(attempt.AttemptId, order.OrderId, order.TotalAmount, order.Currency), cancellationToken);
            }
            catch (ProviderRejectedException)
            {
                // A definitive refusal: nothing exists at the provider, so the attempt can be closed and another provider chosen.
                await store.CancelUnreferencedAttemptAsync(attempt.AttemptId, cancellationToken);
                return Result.Failure<PaymentStartResult>(PaymentsErrors.ProviderRejected);
            }
            catch (Exception ex) when (!CancellationRules.IsCallerCancellation(ex, cancellationToken))
            {
                // Ambiguous (timeout, connection loss, unreadable answer): the provider may have created something. It is NEVER
                // re-tried on another provider. A provider that deduplicates (Stripe) keeps the attempt for an idempotent retry.
                // One that cannot is not asked again: the attempt is closed (own failure code), any payment that nevertheless
                // arrives for it is an operator-visible anomaly, and the customer starts again.
                if (!provider.IdempotentInitiation)
                {
                    using (FinancialTransitionContext.Refine(reason: TransitionReason.InitiationUncertain))
                        await store.CancelUncertainAttemptAsync(attempt.AttemptId, cancellationToken);
                    return Result.Failure<PaymentStartResult>(PaymentsErrors.InitiationUncertain);
                }

                return Result.Failure<PaymentStartResult>(PaymentsErrors.ProviderTemporarilyUnavailable);
            }

            await store.SetProviderSessionAsync(attempt.AttemptId, session.ProviderReference, session.CheckoutUrl, cancellationToken);
            order = await store.GetAsync(order.OrderId, customerId, cancellationToken) ?? order;
            attempt = order.Payment is { } current && current.AttemptId == attempt.AttemptId ? current : attempt;
        }

        return Result.Success(new PaymentStartResult(order, attempt, begin.Outcome == BeginPaymentOutcome.Replay));
    }

    /// <summary>
    /// Applies a provider verdict. Duplicates are safe: the same verdict again re-drives the (idempotent) sale
    /// confirmation, a different verdict for a completed attempt is a conflict, and a verdict that does not match the
    /// stored order/attempt/reference is rejected as not found.
    /// </summary>
    public async Task<Result<Order>> CompleteAsync(PaymentCallback callback, CancellationToken cancellationToken = default)
    {
        var recorded = await store.RecordAttemptOutcomeAsync(callback, cancellationToken);
        if (recorded == RecordAttemptOutcome.NotFound)
            return Result.Failure<Order>(PaymentsErrors.AttemptNotFound);
        if (recorded == RecordAttemptOutcome.ConflictingOutcome)
            return Result.Failure<Order>(PaymentsErrors.AlreadyCompleted);
        if (recorded is RecordAttemptOutcome.LateSuccessRecorded or RecordAttemptOutcome.LateSuccessAlreadyRecorded)
            return Result.Failure<Order>(PaymentsErrors.LateSuccessNeedsReview); // recorded for review; deliberately NOT finalised

        var order = await store.GetAsync(callback.OrderId, null, cancellationToken)
            ?? throw new InvalidOperationException("Order disappeared while completing a payment.");

        if (callback.Succeeded)
            order = await FinalizeSuccessfulAsync(order, cancellationToken);

        return Result.Success(order);
    }

    /// <summary>
    /// The fact "the provider took the money" is already durable. Now try to sell the seats; if the hold expired (or
    /// was released) first, nothing is sold and the order requires a refund. Idempotent: callbacks, retries and the
    /// reconciliation worker all come through here.
    /// </summary>
    public async Task<Order> FinalizeSuccessfulAsync(Order order, CancellationToken cancellationToken = default)
    {
        var sale = await reservations.ConfirmSaleAsync(order.ReservationId, cancellationToken);

        // Paying or refunding the order is this application's own decision (the sale either succeeded or could not be honoured),
        // taken because of a recorded provider fact; the history says so rather than calling it a provider fact.
        using (FinancialTransitionContext.Refine(FactBasis.ApplicationDecision, sale == SaleResult.Confirmed ? TransitionReason.SaleConfirmation : "sale_not_honoured_refund_required"))
            await store.FinalizeAfterSaleAsync(order.OrderId, sale == SaleResult.Confirmed, cancellationToken);
        return await store.GetAsync(order.OrderId, null, cancellationToken) ?? order;
    }
}
