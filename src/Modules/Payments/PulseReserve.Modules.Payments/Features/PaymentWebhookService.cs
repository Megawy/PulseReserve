using Microsoft.Extensions.Logging;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Persistence;

namespace PulseReserve.Modules.Payments.Features;

public enum WebhookOutcome
{
    /// <summary>A verified payment result was recorded and the order lifecycle advanced (Paid, or RefundRequired when the seats were gone).</summary>
    Applied,

    /// <summary>
    /// Verified and nothing more can be gained from a redelivery: a duplicate, an event type we do not use, a conclusive-looking event
    /// that matches nothing and moved no money, or an event that is durably recorded elsewhere (a late-success exception, a refused
    /// conflicting verdict). Answered with 2xx.
    /// </summary>
    Acknowledged,

    /// <summary>
    /// A signature-verified SUCCESS that this system did not apply (its amount, currency, reference or association disagree with our
    /// records) and whose sanitized evidence is now durably stored (or was already). Answered with 2xx: it must not be retried
    /// endlessly, and it is acknowledged only after the record exists. Nothing was paid, ticketed or refunded.
    /// </summary>
    AnomalyRecorded,

    /// <summary>The provider is not enabled here (or does not deliver webhooks).</summary>
    ProviderUnavailable,

    /// <summary>Authenticity could not be established (bad signature, replay, unreadable, other merchant). Nothing was read or changed.</summary>
    Rejected,

    /// <summary>
    /// The event is authentic but this system's OWN data prevents judging it (for example the order of a known attempt is missing or has an
    /// amount that cannot be expressed in minor units). Nothing was recorded, so it must NOT be acknowledged: answered with 5xx so the
    /// provider keeps the event and delivers it again once the data is repaired.
    /// </summary>
    Failed
}

public sealed record WebhookResult(WebhookOutcome Outcome, string Reason);

/// <summary>
/// Applies verified provider callbacks to the ONE existing payment lifecycle. The provider adapter has already proven
/// authenticity (signature, timestamp, merchant context); this service additionally cross-checks the event against
/// PulseReserve's own records and never trusts the event for anything that matters:
/// the attempt must exist and belong to THIS provider, the provider reference must equal the stored one, and a success
/// must carry exactly the order's amount and currency. A verified event that disagrees with our records changes
/// nothing: the attempt stays unresolved and reconciliation (or an operator) decides. A verified success then goes
/// through <see cref="PaymentService.CompleteAsync"/>, so duplicates are idempotent and expired reservations end as
/// RefundRequired exactly as for any other success.
/// </summary>
public sealed class PaymentWebhookService(
    IPaymentProviderRegistry providers,
    IPaymentAttemptLookup attempts,
    IOrderStore orders,
    PaymentService payments,
    IPaymentAnomalyRecorder anomalies,
    ILogger<PaymentWebhookService> logger)
{
    public async Task<WebhookResult> HandleAsync(string providerName, WebhookRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await HandleCoreAsync(providerName, request, cancellationToken);
            PaymentsTelemetry.Webhook(providerName, result.Outcome.ToString());
            return result;
        }
        catch (Exception ex) when (!CancellationRules.IsCallerCancellation(ex, cancellationToken))
        {
            // Not acknowledged: the host answers 5xx and the provider delivers the event again.
            PaymentsTelemetry.Webhook(providerName, "Error");
            throw;
        }
    }

    private async Task<WebhookResult> HandleCoreAsync(string providerName, WebhookRequest request, CancellationToken cancellationToken)
    {
        var provider = providers.Find(providerName);
        if (provider is null || !provider.IsAvailable || provider is not IPaymentWebhookHandler handler)
            return new WebhookResult(WebhookOutcome.ProviderUnavailable, "provider_unavailable");

        var parsed = handler.Parse(request);
        switch (parsed.Status)
        {
            case WebhookVerification.InvalidSignature:
                logger.LogWarning("{Provider} webhook rejected: signature could not be verified", provider.Name);
                return new WebhookResult(WebhookOutcome.Rejected, "invalid_signature");
            case WebhookVerification.Malformed:
                logger.LogWarning("{Provider} webhook rejected: unreadable event", provider.Name);
                return new WebhookResult(WebhookOutcome.Rejected, "malformed_event");
            case WebhookVerification.WrongContext:
                logger.LogWarning("{Provider} webhook rejected: event belongs to a different merchant context", provider.Name);
                return new WebhookResult(WebhookOutcome.Rejected, "wrong_context");
        }

        var evt = parsed.Event!;
        if (evt.Kind == VerifiedEventKind.Ignored)
            return new WebhookResult(WebhookOutcome.Acknowledged, "ignored_event");

        // Everything this event changes from here on is recorded in the transition history as caused by a signature-verified
        // provider event (its id, never its payload).
        using var scope = FinancialTransitionContext.Begin(
            TransitionSource.ProviderWebhook, FactBasis.ProviderVerified, provider.Name, evt.EventId, reason: TransitionReason.ProviderVerdict);

        var attempt = await attempts.GetAttemptAsync(evt.AttemptId, cancellationToken);
        if (attempt is null)
        {
            // Authentic, but about nothing we know (another environment sharing the account, or a deleted test).
            return await NotAppliedAsync(provider.Name, evt, PaymentAnomalyKind.UnknownPaymentReference, "unknown_attempt", null, null, cancellationToken);
        }

        // Cross-provider isolation: a Stripe event can never complete a Paymob attempt (or the reverse).
        if (!string.Equals(attempt.Provider, provider.Name, StringComparison.OrdinalIgnoreCase))
        {
            return await NotAppliedAsync(provider.Name, evt, PaymentAnomalyKind.InconsistentAssociation, "wrong_provider_for_attempt", null, null, cancellationToken);
        }

        if (evt.OrderId is { } claimedOrder && claimedOrder != attempt.OrderId)
        {
            return await NotAppliedAsync(provider.Name, evt, PaymentAnomalyKind.InconsistentAssociation, "order_mismatch", null, null, cancellationToken);
        }

        if (evt.Kind == VerifiedEventKind.PaymentSucceeded)
        {
            var order = await orders.GetAsync(attempt.OrderId, null, cancellationToken);
            if (order is null)
            {
                // The attempt exists but its order cannot be loaded: our own data is inconsistent. Recording nothing and answering 2xx would
                // drop a verified payment, so the event is NOT acknowledged and the provider delivers it again once this is repaired.
                logger.LogError(PaymentsLogEvents.EventNotJudgeable, "{Provider} event {EventId}: the order of attempt {AttemptId} could not be loaded; not acknowledged", provider.Name, evt.EventId, attempt.AttemptId);
                return new WebhookResult(WebhookOutcome.Failed, "order_not_found");
            }

            long expected;
            try
            {
                expected = CurrencyAmounts.ToMinorUnits(order.TotalAmount, order.Currency);
            }
            catch (ArgumentException)
            {
                logger.LogError(PaymentsLogEvents.EventNotJudgeable, "{Provider} event {EventId}: the amount of the order of attempt {AttemptId} cannot be expressed in minor units; not acknowledged", provider.Name, evt.EventId, attempt.AttemptId);
                return new WebhookResult(WebhookOutcome.Failed, "order_amount_invalid");
            }

            if (evt.AmountMinor != expected || !string.Equals(evt.Currency, order.Currency, StringComparison.OrdinalIgnoreCase))
            {
                // Money moved for a different amount or currency than the order: never apply it as the order's payment. A valid
                // signature proves who sent it, not that the amount is right. Nothing is applied; the evidence is kept (once per
                // provider event) for an administrator, with the received values marked untrusted and the expected ones taken from
                // OUR order, and the event is acknowledged only after that record exists.
                return await NotAppliedAsync(provider.Name, evt, PaymentAnomalyKind.AmountOrCurrencyMismatch, "amount_or_currency_mismatch", attempt, order, cancellationToken);
            }
        }

        var callback = new PaymentCallback(
            attempt.AttemptId, attempt.OrderId, evt.ProviderReference, evt.Kind == VerifiedEventKind.PaymentSucceeded, evt.FailureCode, evt.EventId);
        var result = await payments.CompleteAsync(callback, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("{Provider} event {EventId} applied to attempt {AttemptId}; order is {Status}", provider.Name, evt.EventId, attempt.AttemptId, result.Value.Status);
            return new WebhookResult(WebhookOutcome.Applied, "applied");
        }

        if (result.Error == PaymentsErrors.LateSuccessNeedsReview)
        {
            // Verified success (signature, amount and currency all checked above) for an attempt recorded as Failed. The Failed
            // verdict is untouched, the order is not paid, and one durable exception now waits for an administrator. The event is
            // acknowledged only because it is recorded: a database failure surfaces as an error so the provider redelivers.
            logger.LogWarning("{Provider} event {EventId} reports a success for attempt {AttemptId} already recorded as failed; recorded for operator review", provider.Name, evt.EventId, attempt.AttemptId);
            return new WebhookResult(WebhookOutcome.Acknowledged, "late_success_recorded_for_review");
        }

        if (result.Error == PaymentsErrors.AlreadyCompleted)
        {
            // The attempt already holds the opposite verdict. Nothing is overwritten; an operator can see it.
            logger.LogError("{Provider} event {EventId} conflicts with the recorded outcome of attempt {AttemptId}", provider.Name, evt.EventId, attempt.AttemptId);
            return new WebhookResult(WebhookOutcome.Acknowledged, "conflicts_with_recorded_outcome");
        }

        // AttemptNotFound: the provider reference does not match the stored one (or none was ever stored).
        return await NotAppliedAsync(provider.Name, evt, PaymentAnomalyKind.ReferenceMismatch, "reference_mismatch", attempt, null, cancellationToken);
    }

    /// <summary>
    /// A signature-verified event that disagrees with our records and was NOT applied. What the answer must be depends on whether money
    /// may have moved:
    /// <list type="bullet">
    /// <item>A SUCCESS: sanitized evidence is stored first (or confirmed to be stored already), and only then is the event acknowledged
    /// (<see cref="WebhookOutcome.AnomalyRecorded"/>, 2xx). A storage failure throws, so the provider is told to retry; the event is never
    /// acknowledged without its record, and it is never retried once the record exists.</item>
    /// <item>A FAILURE or expiry: no money moved, so there is nothing to record and nothing a redelivery could change. It is acknowledged
    /// and ignored; a 4xx here would only make the provider retry (Stripe: for up to three days in live mode) an event that can never be
    /// processed.</item>
    /// </list>
    /// </summary>
    private async Task<WebhookResult> NotAppliedAsync(
        string provider, VerifiedPaymentEvent evt, string kind, string reasonCode, AttemptRecord? attempt, Order? order, CancellationToken cancellationToken)
    {
        if (evt.Kind != VerifiedEventKind.PaymentSucceeded)
        {
            logger.LogInformation(PaymentsLogEvents.UnmatchedEventIgnored, "{Provider} event {EventId} matches no payment ({Reason}) and moved no money; acknowledged without a record", provider, evt.EventId, reasonCode);
            return new WebhookResult(WebhookOutcome.Acknowledged, "unmatched_event_ignored");
        }

        await RecordAnomalyAsync(provider, evt, kind, reasonCode, attempt, order, cancellationToken);
        return new WebhookResult(WebhookOutcome.AnomalyRecorded, reasonCode);
    }

    /// <summary>
    /// Keeps minimal, sanitized evidence of a SUCCESS event that could not be applied. Only a signature-verified event reaches this
    /// point, and only a success (money may have moved); failure events carry no money and are not recorded. The association with an
    /// attempt and order is set only when the event matched them consistently; the amount and currency from the event are stored as
    /// untrusted received values next to the expected values loaded from the database. A storage failure propagates, so the
    /// webhook is not acknowledged and the provider redelivers. Duplicates are absorbed by the provider event id; the store confirms that
    /// the record exists before reporting a duplicate, so a duplicate is never the reason an event is acknowledged without a record.
    /// </summary>
    private async Task RecordAnomalyAsync(
        string provider, VerifiedPaymentEvent evt, string kind, string reasonCode, AttemptRecord? attempt, Order? order, CancellationToken cancellationToken)
    {
        long? expectedMinor = null;
        string? expectedCurrency = null;
        if (order is not null)
        {
            expectedCurrency = order.Currency.ToUpperInvariant();
            try
            {
                expectedMinor = CurrencyAmounts.ToMinorUnits(order.TotalAmount, order.Currency);
            }
            catch (ArgumentException)
            {
                expectedMinor = null;
            }
        }

        var received = evt.Currency is { Length: 3 } c && c.All(char.IsAsciiLetter) ? c.ToUpperInvariant() : null;
        var record = new PaymentAnomalyRecord(
            kind, reasonCode, provider, evt.EventId, attempt?.AttemptId, attempt?.OrderId, evt.AttemptId == Guid.Empty ? null : evt.AttemptId, evt.OrderId,
            evt.AmountMinor, received, expectedMinor, expectedCurrency);
        using (FinancialTransitionContext.Refine(reason: TransitionReason.PaymentAnomalyRecorded))
        {
            bool stored;
            try
            {
                stored = await anomalies.RecordAsync(record, cancellationToken);
            }
            catch (Exception ex) when (!CancellationRules.IsCallerCancellation(ex, cancellationToken))
            {
                // The type only: the exception text of a storage fault can carry connection details. The host logs the exception itself.
                PaymentsTelemetry.AnomalyPersistenceFailure(provider, kind);
                logger.LogError(
                    PaymentsLogEvents.AnomalyPersistenceFailed,
                    "{Provider} event {EventId} ({Kind}, {Reason}) could not be recorded ({ErrorType}); NOT acknowledged, the provider will deliver it again",
                    provider, evt.EventId, kind, reasonCode, ex.GetType().Name);
                throw;
            }

            if (stored)
            {
                PaymentsTelemetry.AnomalyRecordedCount(provider, kind);
                logger.LogWarning(
                    PaymentsLogEvents.AnomalyRecorded,
                    "{Provider} event {EventId} was not applied ({Kind}, {Reason}) and is recorded for operator review",
                    provider, evt.EventId, kind, reasonCode);
            }
            else
            {
                PaymentsTelemetry.AnomalyDeduplicatedCount(provider, kind);
                logger.LogInformation(
                    PaymentsLogEvents.AnomalyDuplicate,
                    "{Provider} event {EventId} ({Kind}, {Reason}) was already recorded for operator review; duplicate acknowledged",
                    provider, evt.EventId, kind, reasonCode);
            }
        }
    }
}
