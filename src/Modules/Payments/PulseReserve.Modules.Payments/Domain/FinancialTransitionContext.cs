using System.Diagnostics;

namespace PulseReserve.Modules.Payments.Domain;

/// <summary>Who or what caused a financial state change (see <see cref="FinancialTransitionContext"/>).</summary>
public static class TransitionSource
{
    /// <summary>A signature-verified provider webhook.</summary>
    public const string ProviderWebhook = "ProviderWebhook";

    /// <summary>The reconciliation worker asking the provider for the real status of an attempt.</summary>
    public const string ReconciliationLookup = "ReconciliationLookup";

    /// <summary>A background worker pass (discovery, refund processing, escalation).</summary>
    public const string Worker = "Worker";

    public const string Administrator = "Administrator";
    public const string Customer = "Customer";

    /// <summary>The development/test payment simulator.</summary>
    public const string Simulator = "Simulator";

    public const string System = "System";
    public const string Unspecified = "Unspecified";
}

/// <summary>
/// What kind of statement a transition rests on. These are never blended: a provider's verified fact, a decision this
/// application made by its own rules, an administrator's command, and an administrator's statement about something outside the
/// system (which no provider has confirmed).
/// </summary>
public static class FactBasis
{
    public const string ProviderVerified = "ProviderVerified";
    public const string ApplicationDecision = "ApplicationDecision";
    public const string AdministratorAction = "AdministratorAction";
    public const string AdministratorStatement = "AdministratorStatement";
    public const string Unspecified = "Unspecified";
}

/// <summary>Stable reason codes for the transition history (<c>payments.financial_transitions.reason</c>).</summary>
public static class TransitionReason
{
    public const string ProviderVerdict = "provider_verdict";
    public const string LateSuccessRecorded = "late_success_recorded";
    public const string AttemptWithdrawnForReview = "attempt_withdrawn_for_review";
    public const string SaleConfirmation = "sale_confirmation";
    public const string CustomerCancelled = "customer_cancelled";
    public const string DeadlinePassed = "payment_deadline_passed";
    public const string ProviderAnswer = "provider_answer";
    public const string RefundIntent = "refund_intent_recorded";
    public const string NeedsOperator = "needs_operator";
    public const string AdministratorRetry = "administrator_retry";
    public const string AdministratorRequest = "administrator_request";
    public const string ManualRefundStatement = "manual_refund_statement";
    public const string ExceptionResolutionStatement = "exception_resolution_statement";
    public const string AnomalyResolutionStatement = "anomaly_resolution_statement";
    public const string StaleAttemptCleanup = "stale_attempt_cleanup";
    public const string InitiationUncertain = "initiation_uncertain";
    public const string PaymentAnomalyRecorded = "payment_anomaly_recorded";
}

/// <summary>What the store tells the database about the operation it is about to run.</summary>
public sealed record TransitionInfo(
    string Source, string FactBasis, string? Provider, string? ProviderEventId, Guid? ActorUserId, string? Reason, string CorrelationId);

/// <summary>
/// Ambient, per-request/per-worker-pass description of WHY the financial state is about to change. The stores copy it into
/// transaction-local database settings at the start of every transaction, and a database trigger writes it, together with the
/// previous and new state, into the append-only <c>payments.financial_transitions</c> table in the SAME transaction as the
/// change. A rolled-back change therefore leaves no history, and a change made without any scope is still recorded (source
/// <c>Unspecified</c>), because the trigger does not depend on the application remembering to log.
/// <para>
/// It carries only identifiers and codes: never a payload, signature, secret, reference or customer detail.
/// </para>
/// </summary>
public static class FinancialTransitionContext
{
    private static readonly AsyncLocal<TransitionInfo?> Holder = new();

    public static TransitionInfo? Current => Holder.Value;

    /// <summary>Starts a scope. Disposing it restores the enclosing one. The correlation id is inherited, else the trace id, else new.</summary>
    public static IDisposable Begin(
        string source, string factBasis, string? provider = null, string? providerEventId = null, Guid? actorUserId = null, string? reason = null)
    {
        var previous = Holder.Value;
        var correlation = previous?.CorrelationId ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        Holder.Value = new TransitionInfo(source, factBasis, provider ?? previous?.Provider, providerEventId ?? previous?.ProviderEventId, actorUserId ?? previous?.ActorUserId, reason, correlation);
        return new Scope(previous);
    }

    /// <summary>Overrides the fact basis and/or reason inside the current scope for one operation (for example "this move rests on a provider answer").</summary>
    public static IDisposable Refine(string? factBasis = null, string? reason = null, string? providerEventId = null)
    {
        var previous = Holder.Value;
        var baseInfo = previous ?? new TransitionInfo(TransitionSource.Unspecified, FactBasis.Unspecified, null, null, null, null, Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"));
        Holder.Value = baseInfo with { FactBasis = factBasis ?? baseInfo.FactBasis, Reason = reason ?? baseInfo.Reason, ProviderEventId = providerEventId ?? baseInfo.ProviderEventId };
        return new Scope(previous);
    }

    private sealed class Scope(TransitionInfo? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Holder.Value = previous;
        }
    }
}
