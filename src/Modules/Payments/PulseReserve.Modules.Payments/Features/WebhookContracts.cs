namespace PulseReserve.Modules.Payments.Features;

/// <summary>A webhook delivery exactly as received: the raw body (signatures cover the exact bytes) plus headers and query.</summary>
public sealed record WebhookRequest(
    string RawBody, IReadOnlyDictionary<string, string> Headers, IReadOnlyDictionary<string, string> Query)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public string? QueryValue(string name) =>
        Query.FirstOrDefault(q => string.Equals(q.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

public enum WebhookVerification
{
    /// <summary>The signature is valid and the event carries everything needed.</summary>
    Verified,

    /// <summary>The signature is missing, malformed, wrong, or too old. Nothing in the body may be trusted.</summary>
    InvalidSignature,

    /// <summary>The signature is valid but the body is not an event this integration can read.</summary>
    Malformed,

    /// <summary>The signature is valid but the event belongs to another merchant context (mode, account, integration).</summary>
    WrongContext
}

public enum VerifiedEventKind
{
    /// <summary>Not relevant to PulseReserve (or not conclusive): acknowledged and ignored.</summary>
    Ignored,

    /// <summary>The provider says the payment was captured.</summary>
    PaymentSucceeded,

    /// <summary>The provider says the payment can no longer succeed (for example the checkout session expired).</summary>
    PaymentFailed
}

/// <summary>
/// A provider event whose authenticity was verified, reduced to the facts PulseReserve checks against its OWN records
/// before acting: which attempt, which provider reference, and the amount and currency the provider saw.
/// </summary>
public sealed record VerifiedPaymentEvent(
    string EventId,
    VerifiedEventKind Kind,
    Guid AttemptId,
    Guid? OrderId,
    string ProviderReference,
    long? AmountMinor,
    string? Currency,
    string? FailureCode);

public sealed record WebhookParseResult(WebhookVerification Status, VerifiedPaymentEvent? Event)
{
    public static WebhookParseResult Invalid() => new(WebhookVerification.InvalidSignature, null);

    public static WebhookParseResult Malformed() => new(WebhookVerification.Malformed, null);

    public static WebhookParseResult WrongContext() => new(WebhookVerification.WrongContext, null);

    public static WebhookParseResult Ignored(string eventId) =>
        new(WebhookVerification.Verified, new VerifiedPaymentEvent(eventId, VerifiedEventKind.Ignored, Guid.Empty, null, string.Empty, null, null, null));
}

/// <summary>Implemented by providers that deliver payment results by signed webhook.</summary>
public interface IPaymentWebhookHandler
{
    /// <summary>Verifies the delivery's authenticity and reduces it to a <see cref="VerifiedPaymentEvent"/>. Pure: no network, no state.</summary>
    WebhookParseResult Parse(WebhookRequest request);
}
