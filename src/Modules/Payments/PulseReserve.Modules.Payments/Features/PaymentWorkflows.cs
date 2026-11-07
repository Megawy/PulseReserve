using Microsoft.Extensions.Logging;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Payments.Features;

/// <summary>
/// Finds and repairs half-finished payment work. Every repair re-uses the existing idempotent operations
/// (payment completion, reservation sale confirmation, order finalisation), so running it twice, concurrently, or
/// after a crash is safe. It NEVER infers a payment verdict: a timeout or any ambiguous provider answer is
/// "unknown" and only leads to a later retry; success is recorded solely when the provider says so.
/// Logs carry ids, kinds and outcomes only: no tokens, references, cookies or personal data.
/// </summary>
public sealed class ReconciliationService(
    IWorkflowStore workflow,
    IOrderStore orders,
    IPaymentProviderRegistry providers,
    IReservationCheckoutGateway reservations,
    PaymentService payments,
    WorkflowOptions options,
    ILogger<ReconciliationService> logger)
{
    private sealed record Handled(bool Done, string Outcome, string? Error = null);

    public async Task<(int Discovered, int Processed, int Failed)> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = FinancialTransitionContext.Begin(TransitionSource.Worker, FactBasis.ApplicationDecision);
        var discovered = await workflow.DiscoverReconciliationTasksAsync(options, cancellationToken);
        discovered += await workflow.EnsureRefundsForRefundRequiredOrdersAsync(cancellationToken);

        int processed = 0, failed = 0;
        foreach (var task in await workflow.ClaimTasksAsync(options, cancellationToken))
        {
            // One poison task must never stop the rest of the batch: every task is isolated.
            try
            {
                var result = await HandleAsync(task, cancellationToken);
                if (result.Done)
                {
                    await workflow.CompleteTaskAsync(task.Id, result.Outcome, cancellationToken);
                    processed++;
                }
                else
                {
                    await workflow.RetryTaskLaterAsync(task.Id, result.Outcome, result.Error, options, cancellationToken);
                    failed++;
                }
            }
            catch (Exception ex) when (!CancellationRules.IsCallerCancellation(ex, cancellationToken))
            {
                failed++;
                logger.LogWarning("Reconciliation task {TaskId} ({Kind}) failed on attempt {Attempt}: {ErrorType}", task.Id, task.Kind, task.Attempts, ex.GetType().Name);
                await workflow.RetryTaskLaterAsync(task.Id, "Unhandled error", Describe(ex), options, cancellationToken);
            }
        }

        return (discovered, processed, failed);
    }

    private Task<Handled> HandleAsync(ReconciliationTask task, CancellationToken cancellationToken) => task.Kind switch
    {
        ReconciliationKind.FinalizeSuccessfulPayment => FinalizeAsync(task, cancellationToken),
        ReconciliationKind.ResolveStaleAttempt => ResolveStaleAsync(task, cancellationToken),
        ReconciliationKind.ExpireOrder => ExpireAsync(task, cancellationToken),
        _ => Task.FromResult(new Handled(false, "Unknown task kind", task.Kind))
    };

    // The provider took the money (recorded) but the order was never finalized: run the idempotent sale
    // confirmation again. If the hold expired or the seats were reassigned, it ends as RefundRequired, never Paid.
    private async Task<Handled> FinalizeAsync(ReconciliationTask task, CancellationToken cancellationToken)
    {
        var attempt = await workflow.GetAttemptAsync(task.SubjectId, cancellationToken);
        if (attempt is null)
            return new Handled(true, "Attempt not found");
        if (attempt.Status != PaymentAttemptStatus.Succeeded)
            return new Handled(true, "Attempt is not succeeded");

        var order = await orders.GetAsync(attempt.OrderId, null, cancellationToken);
        if (order is null)
            return new Handled(true, "Order not found");
        if (order.Status is OrderStatus.Paid or OrderStatus.RefundRequired or OrderStatus.Refunded)
            return new Handled(true, "Already finalized");

        var finalized = await payments.FinalizeSuccessfulAsync(order, cancellationToken);
        logger.LogInformation("Reconciliation finalized order {OrderId} as {Status}", order.OrderId, finalized.Status);
        return new Handled(true, $"Finalized as {finalized.Status}");
    }

    // An attempt has been Initiated for too long. Ask the provider; never guess.
    private async Task<Handled> ResolveStaleAsync(ReconciliationTask task, CancellationToken cancellationToken)
    {
        var attempt = await workflow.GetAttemptAsync(task.SubjectId, cancellationToken);
        if (attempt is null || attempt.Status != PaymentAttemptStatus.Initiated)
            return new Handled(true, "Attempt no longer in flight");

        // Always the provider the attempt was created with: an attempt is never re-examined (or re-charged) elsewhere.
        var provider = providers.Find(attempt.Provider);
        if (provider is null)
            return new Handled(false, "The attempt's provider is not configured", attempt.Provider);

        ProviderPaymentState state;
        if (attempt.ProviderReference is null && !provider.SupportsPaymentLookup)
        {
            // Never obtained a reference, so no checkout URL was ever shown to the customer: nothing can be paid.
            state = ProviderPaymentState.NotFound;
        }
        else if (!provider.SupportsPaymentLookup)
        {
            // No verified lookup exists for this provider: the outcome stays unresolved until a verified webhook (or an
            // operator) decides. It is neither success nor failure, and the abandonment rules below still apply.
            state = ProviderPaymentState.Pending;
        }
        else
        {
            try
            {
                var order = await orders.GetAsync(attempt.OrderId, null, cancellationToken);
                state = order is null
                    ? ProviderPaymentState.NotFound
                    : await provider.GetPaymentStatusAsync(
                        new PaymentStatusQuery(attempt.AttemptId, attempt.ProviderReference, order.OrderId, order.TotalAmount, order.Currency),
                        cancellationToken);
            }
            catch (Exception ex) when (!CancellationRules.IsCallerCancellation(ex, cancellationToken))
            {
                // Timeout / connection loss / provider error / data that does not match the order: the outcome is
                // UNKNOWN, not "failed" and not "paid".
                return new Handled(false, "Provider status unknown", Describe(ex));
            }
        }

        switch (state)
        {
            case ProviderPaymentState.Succeeded or ProviderPaymentState.Failed when attempt.ProviderReference is not null:
            {
                // The provider's own answer to a status lookup (reference, amount and currency were verified by the adapter).
                using var lookup = FinancialTransitionContext.Begin(
                    TransitionSource.ReconciliationLookup, FactBasis.ProviderVerified, attempt.Provider, reason: TransitionReason.ProviderVerdict);
                var result = await payments.CompleteAsync(
                    new PaymentCallback(attempt.AttemptId, attempt.OrderId, attempt.ProviderReference, state == ProviderPaymentState.Succeeded,
                        state == ProviderPaymentState.Failed ? "provider_reported_failure" : null),
                    cancellationToken);
                return result.IsSuccess
                    ? new Handled(true, $"Provider reported {state}")
                    : new Handled(false, "Could not record the provider verdict", result.Error.Code);
            }
            case ProviderPaymentState.NotFound when attempt.ProviderReference is null:
                using (FinancialTransitionContext.Refine(reason: TransitionReason.StaleAttemptCleanup))
                    await workflow.CancelAttemptAsync(attempt.AttemptId, cancellationToken); // never reached the provider
                return new Handled(true, "Attempt never reached the provider; cancelled");
            case ProviderPaymentState.Pending:
            {
                var order = await orders.GetAsync(attempt.OrderId, null, cancellationToken);
                var abandoned = order is not null && order.Status != OrderStatus.PendingPayment
                                && attempt.Age >= options.AbandonAttemptAfter; // age measured by the database clock
                if (!abandoned)
                    return new Handled(false, "Provider still pending");
                // Stop tracking, but a late verdict is still accepted by the callback (and ends as RefundRequired).
                using (FinancialTransitionContext.Refine(reason: TransitionReason.StaleAttemptCleanup))
                    await workflow.AbandonAttemptAsync(attempt.AttemptId, cancellationToken);
                return new Handled(true, "Abandoned: unpayable order, provider never answered");
            }
            default:
                return new Handled(false, $"Provider answered {state} without a matching local record");
        }
    }

    // A pending order is past its deadline: persist Expired and let the reservation close (idempotent, DB clock).
    private async Task<Handled> ExpireAsync(ReconciliationTask task, CancellationToken cancellationToken)
    {
        (Guid ReservationId, Guid CustomerId)? closed;
        using (FinancialTransitionContext.Refine(reason: TransitionReason.DeadlinePassed))
            closed = await workflow.ExpireOrderAsync(task.SubjectId, cancellationToken);
        if (closed is null)
            return new Handled(true, "Order is no longer expirable");

        await reservations.ReleaseAsync(closed.Value.ReservationId, closed.Value.CustomerId, cancellationToken);
        return new Handled(true, "Expired");
    }

    internal static string Describe(Exception ex) => $"{ex.GetType().Name}: {Sanitize(ex.Message)}";

    private static string Sanitize(string message) => message.Length > 200 ? message[..200] : message;
}

/// <summary>
/// Drives refunds through their state machine. The provider is never called twice for the same refund blindly:
/// the intent is persisted as Unknown BEFORE the call, an ambiguous answer leaves it Unknown, and Unknown is resolved
/// by QUERYING the provider with the refund's idempotency key. Only a definitive "no such refund" permits sending
/// the request again (with the same key, so a provider that did receive it still deduplicates). A refund is
/// Succeeded only when the provider confirms it.
/// </summary>
public sealed class RefundService(
    IWorkflowStore workflow,
    IPaymentProviderRegistry providers,
    WorkflowOptions options,
    ILogger<RefundService> logger)
{
    public async Task<(int Processed, int Failed)> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = FinancialTransitionContext.Begin(TransitionSource.Worker, FactBasis.ApplicationDecision);
        int processed = 0, failed = 0;
        foreach (var refund in await workflow.ClaimRefundsAsync(options, cancellationToken))
        {
            try
            {
                await ProcessAsync(refund, cancellationToken);
                processed++;
            }
            catch (Exception ex) when (!CancellationRules.IsCallerCancellation(ex, cancellationToken))
            {
                failed++;
                logger.LogWarning("Refund {RefundId} failed on attempt {Attempt}: {ErrorType}", refund.RefundId, refund.Attempts, ex.GetType().Name);
                await workflow.DeferRefundAsync(refund.RefundId, ReconciliationService.Describe(ex), options, cancellationToken);
            }
        }

        return (processed, failed);
    }

    private async Task ProcessAsync(Refund refund, CancellationToken cancellationToken)
    {
        // A refund always goes back through the provider that took the payment.
        var provider = providers.Find(refund.Provider)
            ?? throw new InvalidOperationException($"The refund's provider '{refund.Provider}' is not configured.");
        if (!provider.SupportsRefunds)
        {
            using (FinancialTransitionContext.Refine(FactBasis.ApplicationDecision, TransitionReason.NeedsOperator))
                await workflow.MarkRefundNeedsReviewAsync(
                    refund.RefundId, $"{provider.Name} refunds are not available through the API: refund the payment in the provider dashboard.", cancellationToken);
            logger.LogWarning("Refund {RefundId} needs a manual refund with provider {Provider}", refund.RefundId, provider.Name);
            return;
        }

        if (refund.State == RefundState.Requested)
        {
            // Persist the intent first: if we die during the provider call, the row says Unknown and we ask before acting.
            bool recorded;
            using (FinancialTransitionContext.Refine(FactBasis.ApplicationDecision, TransitionReason.RefundIntent))
                recorded = await workflow.MarkRefundUnknownAsync(refund.RefundId, cancellationToken);
            if (!recorded)
                return; // someone else changed it meanwhile
            await SubmitAsync(refund, provider, cancellationToken);
            return;
        }

        ProviderRefundState status;
        try
        {
            var attempt = await workflow.GetAttemptAsync(refund.PaymentAttemptId, cancellationToken);
            status = await provider.GetRefundStatusAsync(
                new RefundStatusQuery(refund.IdempotencyKey, refund.RefundId, attempt?.ProviderReference), cancellationToken);
        }
        catch (Exception ex) when (!CancellationRules.IsCallerCancellation(ex, cancellationToken))
        {
            await workflow.DeferRefundAsync(refund.RefundId, ReconciliationService.Describe(ex), options, cancellationToken);
            return;
        }

        if (status == ProviderRefundState.NotFound && refund.State == RefundState.Unknown)
        {
            // The provider never received it: submitting (same idempotency key) cannot double-refund. The submission records its
            // own moves (it labels the provider's answer itself).
            await SubmitAsync(refund, provider, cancellationToken);
            return;
        }

        switch (status)
        {
            case ProviderRefundState.Succeeded:
                using (FinancialTransitionContext.Refine(FactBasis.ProviderVerified, TransitionReason.ProviderAnswer))
                    await workflow.MarkRefundSucceededAsync(refund.RefundId, refund.ProviderRefundId, cancellationToken);
                logger.LogInformation("Refund {RefundId} confirmed by the provider", refund.RefundId);
                break;
            case ProviderRefundState.Failed:
                using (FinancialTransitionContext.Refine(FactBasis.ProviderVerified, TransitionReason.ProviderAnswer))
                    await workflow.MarkRefundRejectedAsync(refund.RefundId, "Provider reported the refund failed", retryable: false, options, cancellationToken);
                break;
            case ProviderRefundState.Pending:
                using (FinancialTransitionContext.Refine(FactBasis.ProviderVerified, TransitionReason.ProviderAnswer))
                    await workflow.MarkRefundPendingAsync(refund.RefundId, refund.ProviderRefundId, options, cancellationToken);
                break;
            default:
                // No usable answer: only the attempt limit can move this (to review), and that is this application's decision.
                using (FinancialTransitionContext.Refine(FactBasis.ApplicationDecision, TransitionReason.NeedsOperator))
                    await workflow.DeferRefundAsync(refund.RefundId, "Provider has no record of a refund it had accepted", options, cancellationToken);
                break;
        }
    }

    private async Task SubmitAsync(Refund refund, IPaymentProvider provider, CancellationToken cancellationToken)
    {
        var attempt = await workflow.GetAttemptAsync(refund.PaymentAttemptId, cancellationToken);
        if (attempt?.ProviderReference is null)
        {
            await workflow.MarkRefundRejectedAsync(refund.RefundId, "The payment has no provider reference", retryable: false, options, cancellationToken);
            return;
        }

        ProviderRefundResult result;
        try
        {
            result = await provider.RefundAsync(
                new ProviderRefundRequest(refund.RefundId, refund.IdempotencyKey, attempt.ProviderReference, refund.Amount, refund.Currency),
                cancellationToken);
        }
        catch (Exception ex) when (!CancellationRules.IsCallerCancellation(ex, cancellationToken))
        {
            // Ambiguous: the provider may or may not have refunded. Stay Unknown; the next pass queries first.
            await workflow.DeferRefundAsync(refund.RefundId, ReconciliationService.Describe(ex), options, cancellationToken);
            return;
        }

        using var answer = FinancialTransitionContext.Refine(FactBasis.ProviderVerified, TransitionReason.ProviderAnswer);
        if (result.Accepted && result.Completed)
        {
            await workflow.MarkRefundSucceededAsync(refund.RefundId, result.ProviderRefundId, cancellationToken);
            logger.LogInformation("Refund {RefundId} confirmed by the provider", refund.RefundId);
        }
        else if (result.Accepted)
        {
            await workflow.MarkRefundPendingAsync(refund.RefundId, result.ProviderRefundId, options, cancellationToken);
        }
        else
        {
            await workflow.MarkRefundRejectedAsync(refund.RefundId, result.RejectionCode, result.Retryable, options, cancellationToken);
        }
    }
}

/// <summary>
/// Delivers outbox events at least once. Each handler is idempotent (tickets: unique (order, seat); notification:
/// unique (order, kind) delivery record), so redelivery after a crash or lease expiry is harmless. Ticket issuance
/// and notification are separate events: a failing notification never blocks tickets and can never undo the purchase.
/// </summary>
public sealed class OutboxProcessor(
    IWorkflowStore workflow,
    IOrderStore orders,
    INotificationSender notifications,
    WorkflowOptions options,
    ILogger<OutboxProcessor> logger)
{
    public async Task<(int Processed, int Failed)> RunAsync(CancellationToken cancellationToken = default)
    {
        int processed = 0, failed = 0;
        foreach (var evt in await workflow.ClaimOutboxAsync(options, cancellationToken))
        {
            try
            {
                var error = await HandleAsync(evt, cancellationToken);
                if (error is null)
                {
                    await workflow.CompleteOutboxAsync(evt.Id, cancellationToken);
                    processed++;
                }
                else
                {
                    failed++;
                    await workflow.RetryOutboxLaterAsync(evt.Id, error, options, cancellationToken);
                }
            }
            catch (Exception ex) when (!CancellationRules.IsCallerCancellation(ex, cancellationToken))
            {
                failed++;
                logger.LogWarning("Outbox event {EventId} ({Type}) failed on attempt {Attempt}: {ErrorType}", evt.Id, evt.Type, evt.Attempts, ex.GetType().Name);
                await workflow.RetryOutboxLaterAsync(evt.Id, ReconciliationService.Describe(ex), options, cancellationToken);
            }
        }

        return (processed, failed);
    }

    /// <summary>Returns null on success, otherwise a safe error text (the event is retried with backoff).</summary>
    private async Task<string?> HandleAsync(OutboxEvent evt, CancellationToken cancellationToken)
    {
        switch (evt.Type)
        {
            case OutboxEventType.IssueTickets:
                // Tickets exist only for a Paid order (also enforced by a database trigger).
                return await workflow.IssueTicketsAsync(evt.AggregateId, cancellationToken) is null ? "Order is not paid; no tickets issued" : null;

            case OutboxEventType.NotifyOrderPaid:
            {
                var order = await orders.GetAsync(evt.AggregateId, null, cancellationToken);
                if (order is null || order.Status != OrderStatus.Paid)
                    return "Order is not paid; nothing to announce";
                if (await workflow.HasNotificationAsync(order.OrderId, evt.Type, cancellationToken))
                    return null; // redelivery: already sent
                if (!notifications.IsAvailable)
                    return "No notification adapter is configured";

                var tickets = await workflow.ListTicketsAsync(order.OrderId, null, cancellationToken);
                await notifications.SendAsync(new NotificationRequest(order.CustomerId, order.OrderId, "OrderPaid", tickets.Count), cancellationToken);
                await workflow.RecordNotificationAsync(order.OrderId, evt.Type, notifications.Channel, cancellationToken);
                return null;
            }

            default:
                return $"Unknown event type {evt.Type}";
        }
    }
}

/// <summary>One pass of every durable workflow. Safe to run concurrently with other passes and other processes.</summary>
public sealed class PaymentsWorkflow(
    ReconciliationService reconciliation, RefundService refunds, OutboxProcessor outbox)
{
    public async Task<WorkflowRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = FinancialTransitionContext.Begin(TransitionSource.Worker, FactBasis.ApplicationDecision);
        // Order matters only for latency: finalisation creates outbox/refund work that the next steps pick up.
        var rec = await reconciliation.RunAsync(cancellationToken);
        var ref_ = await refunds.RunAsync(cancellationToken);
        var box = await outbox.RunAsync(cancellationToken);
        return new WorkflowRunResult(rec.Discovered, rec.Processed, rec.Failed, ref_.Processed, ref_.Failed, box.Processed, box.Failed);
    }
}

/// <summary>Administrator operations. Callers must already be authorised (Admin policy); every change is audited.</summary>
public sealed class PaymentOperationsService(IWorkflowStore workflow, IPaymentProviderRegistry providers)
{
    /// <summary>The configured providers and what the system can do with each (so "needs a person" states can be explained).</summary>
    public IReadOnlyList<OperationsProvider> Providers() =>
        providers.All
            .Select(p => new OperationsProvider(p.Name, p.IsSimulated, p.IsAvailable, p.SupportsPaymentLookup, p.SupportsRefunds) { SupportsIdempotentInitiation = p.IdempotentInitiation })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public Task<WorkflowSummary> SummaryAsync(CancellationToken cancellationToken = default) => workflow.GetSummaryAsync(cancellationToken);

    public async Task<IReadOnlyList<ReconciliationTask>> TasksAsync(string? state, int limit, CancellationToken cancellationToken = default)
    {
        var tasks = await workflow.ListTasksAsync(state, limit, cancellationToken);

        // Tasks about a payment attempt name the attempt; look up its provider (read-only, bounded by the page size).
        var enriched = new List<ReconciliationTask>(tasks.Count);
        foreach (var task in tasks)
        {
            var aboutAnAttempt = task.Kind is ReconciliationKind.FinalizeSuccessfulPayment or ReconciliationKind.ResolveStaleAttempt;
            var attempt = aboutAnAttempt ? await workflow.GetAttemptAsync(task.SubjectId, cancellationToken) : null;
            enriched.Add(attempt is null ? task : task with { Provider = attempt.Provider });
        }

        return enriched;
    }

    public Task<IReadOnlyList<Refund>> RefundsAsync(string? state, int limit, CancellationToken cancellationToken = default) =>
        workflow.ListRefundsAsync(state, limit, cancellationToken);

    public Task<IReadOnlyList<OutboxEvent>> OutboxAsync(string? state, int limit, CancellationToken cancellationToken = default) =>
        workflow.ListOutboxAsync(state, limit, cancellationToken);

    /// <summary>
    /// Records that an administrator performed a refund in an external dashboard. Allowed only when the refund's payment
    /// provider (taken from the STORED refund, never from the request) is registered and cannot refund through the API; the
    /// state, amount and currency are then verified under a row lock by the store. No provider is ever called. The result is
    /// a ManuallyRecorded refund: explicitly not a provider-confirmed one.
    /// </summary>
    public async Task<Result<Refund>> RecordManualRefundAsync(Guid refundId, ManualRefundRecord record, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        using var scope = FinancialTransitionContext.Begin(
            TransitionSource.Administrator, FactBasis.AdministratorStatement, actorUserId: adminUserId, reason: TransitionReason.ManualRefundStatement);
        if (!record.ConfirmedPerformedExternally)
            return Result.Failure<Refund>(PaymentsErrors.ManualRefundNotConfirmed);
        if (record.Amount <= 0 || record.Currency is not { Length: 3 } || !record.Currency.All(char.IsAsciiLetterUpper))
            return Result.Failure<Refund>(PaymentsErrors.ManualRefundInvalid);
        if (record.ExternalReference is { Length: > 200 } || record.Note is { Length: > 300 })
            return Result.Failure<Refund>(PaymentsErrors.ManualRefundInvalid);

        var refund = await workflow.GetRefundAsync(refundId, cancellationToken);
        if (refund is null)
            return Result.Failure<Refund>(PaymentsErrors.NotFoundForOperation);

        // A manual record is only for a provider this system KNOWS cannot refund through its API. An unknown provider is not
        // assumed manual, and an automatic provider (Stripe) keeps its provider-confirmed path: a person recording it by
        // hand could lead to a second, automatic refund.
        var provider = providers.Find(refund.Provider);
        if (provider is null || provider.SupportsRefunds)
            return Result.Failure<Refund>(PaymentsErrors.ManualRefundNotAllowed);

        var result = await workflow.RecordManualRefundAsync(refundId, adminUserId, record, cancellationToken);
        return result.Outcome switch
        {
            ManualRefundOutcome.Recorded or ManualRefundOutcome.Replay => Result.Success(result.Refund!),
            ManualRefundOutcome.NotFound => Result.Failure<Refund>(PaymentsErrors.NotFoundForOperation),
            ManualRefundOutcome.AmountMismatch => Result.Failure<Refund>(PaymentsErrors.ManualRefundMismatch),
            ManualRefundOutcome.Conflict => Result.Failure<Refund>(PaymentsErrors.ManualRefundConflict),
            _ => Result.Failure<Refund>(PaymentsErrors.ManualRefundNotAllowed)
        };
    }

    public Task<IReadOnlyList<PaymentAnomaly>> AnomaliesAsync(string? state, int limit, CancellationToken cancellationToken = default) =>
        workflow.ListAnomaliesAsync(state, limit, cancellationToken);

    /// <summary>
    /// Records an administrator's statement about the outcome of investigating an anomaly. It is not provider verification: no provider
    /// is called, and the order, attempt, refund, reservation and tickets are not touched.
    /// </summary>
    public async Task<Result<PaymentAnomaly>> ResolveAnomalyAsync(
        Guid anomalyId, PaymentAnomalyResolutionRecord record, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        using var scope = FinancialTransitionContext.Begin(
            TransitionSource.Administrator, FactBasis.AdministratorStatement, actorUserId: adminUserId, reason: TransitionReason.AnomalyResolutionStatement);
        if (!record.InvestigationConfirmed)
            return Result.Failure<PaymentAnomaly>(PaymentsErrors.AnomalyNotConfirmed);
        if (!PaymentAnomalyResolution.All.Contains(record.Resolution))
            return Result.Failure<PaymentAnomaly>(PaymentsErrors.AnomalyResolutionInvalid);
        if (record.ExternalReference is { Length: > 200 } || record.Note is { Length: > 300 })
            return Result.Failure<PaymentAnomaly>(PaymentsErrors.AnomalyResolutionInvalid);

        var result = await workflow.ResolveAnomalyAsync(anomalyId, adminUserId, record, cancellationToken);
        return result.Outcome switch
        {
            PaymentAnomalyResolveOutcome.Resolved or PaymentAnomalyResolveOutcome.Replay => Result.Success(result.Anomaly!),
            PaymentAnomalyResolveOutcome.NotFound => Result.Failure<PaymentAnomaly>(PaymentsErrors.NotFoundForOperation),
            _ => Result.Failure<PaymentAnomaly>(PaymentsErrors.AnomalyResolutionConflict)
        };
    }

    public Task<IReadOnlyList<PaymentException>> ExceptionsAsync(string? state, int limit, CancellationToken cancellationToken = default) =>
        workflow.ListExceptionsAsync(state, limit, cancellationToken);

    /// <summary>
    /// Records an administrator's statement that the money for an exception's payment was returned outside this system. It
    /// asserts nothing about the provider: no provider is called and the order, reservation, tickets and refund records are not
    /// touched. The exception itself, its provider, order, amount and currency come only from the stored row.
    /// </summary>
    public async Task<Result<PaymentException>> ResolveExceptionAsync(
        Guid exceptionId, PaymentExceptionResolutionRecord record, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        using var scope = FinancialTransitionContext.Begin(
            TransitionSource.Administrator, FactBasis.AdministratorStatement, actorUserId: adminUserId, reason: TransitionReason.ExceptionResolutionStatement);
        if (!record.RefundPerformedExternally)
            return Result.Failure<PaymentException>(PaymentsErrors.ExceptionNotConfirmed);
        if (record.Amount <= 0 || record.Currency is not { Length: 3 } || !record.Currency.All(char.IsAsciiLetterUpper))
            return Result.Failure<PaymentException>(PaymentsErrors.ExceptionResolutionInvalid);
        if (record.ExternalReference is { Length: > 200 } || record.Note is { Length: > 300 })
            return Result.Failure<PaymentException>(PaymentsErrors.ExceptionResolutionInvalid);

        var result = await workflow.ResolveExceptionAsync(exceptionId, adminUserId, record, cancellationToken);
        return result.Outcome switch
        {
            PaymentExceptionResolveOutcome.Resolved or PaymentExceptionResolveOutcome.Replay => Result.Success(result.Exception!),
            PaymentExceptionResolveOutcome.NotFound => Result.Failure<PaymentException>(PaymentsErrors.NotFoundForOperation),
            PaymentExceptionResolveOutcome.AmountMismatch => Result.Failure<PaymentException>(PaymentsErrors.ExceptionAmountMismatch),
            _ => Result.Failure<PaymentException>(PaymentsErrors.ExceptionResolutionConflict)
        };
    }

    public Task<IReadOnlyList<AdminOrder>> OrdersAsync(string? status, int limit, CancellationToken cancellationToken = default) =>
        workflow.ListOrdersForOperationsAsync(status, limit, cancellationToken);

    public Task<AdminOrder?> OrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        workflow.GetOrderForOperationsAsync(orderId, cancellationToken);

    public Task<IReadOnlyList<FinancialTransition>> HistoryAsync(Guid? orderId, string? entityType, Guid? entityId, int limit, CancellationToken cancellationToken = default) =>
        workflow.ListTransitionsAsync(orderId, entityType, entityId, limit, cancellationToken);

    public Task<IReadOnlyList<AuditEntry>> AuditAsync(int limit, CancellationToken cancellationToken = default) =>
        workflow.ListAuditAsync(limit, cancellationToken);

    public async Task<Result> RetryTaskAsync(Guid id, Guid adminUserId, CancellationToken cancellationToken = default) =>
        ToResult(await workflow.RetryTaskAsync(id, adminUserId, cancellationToken));

    public async Task<Result> RetryOutboxAsync(Guid id, Guid adminUserId, CancellationToken cancellationToken = default) =>
        ToResult(await workflow.RetryOutboxAsync(id, adminUserId, cancellationToken));

    public async Task<Result> RetryRefundAsync(Guid id, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        using var scope = FinancialTransitionContext.Begin(
            TransitionSource.Administrator, FactBasis.AdministratorAction, actorUserId: adminUserId, reason: TransitionReason.AdministratorRetry);
        return ToResult(await workflow.RetryRefundAsync(id, adminUserId, cancellationToken));
    }

    public async Task<Result<Refund>> RequestRefundAsync(Guid orderId, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        using var scope = FinancialTransitionContext.Begin(
            TransitionSource.Administrator, FactBasis.AdministratorAction, actorUserId: adminUserId, reason: TransitionReason.AdministratorRequest);
        var (result, refund) = await workflow.RequestRefundAsync(orderId, adminUserId, cancellationToken);
        return result.Outcome switch
        {
            OperationOutcome.Done when refund is not null => Result.Success(refund),
            OperationOutcome.NotFound => Result.Failure<Refund>(PaymentsErrors.NotFoundForOperation),
            _ => Result.Failure<Refund>(PaymentsErrors.InvalidOperationState)
        };
    }

    private static Result ToResult(OperationResult result) => result.Outcome switch
    {
        OperationOutcome.Done => Result.Success(),
        OperationOutcome.NotFound => Result.Failure(PaymentsErrors.NotFoundForOperation),
        _ => Result.Failure(PaymentsErrors.InvalidOperationState)
    };
}
