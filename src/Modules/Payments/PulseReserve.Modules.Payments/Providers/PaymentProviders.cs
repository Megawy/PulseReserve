using System.Security.Cryptography;
using PulseReserve.Modules.Payments.Features;

namespace PulseReserve.Modules.Payments.Providers;

/// <summary>
/// DEVELOPMENT/TEST ONLY payment simulator. It takes no card data, moves no money, and "collects" nothing: a payment
/// it reports as succeeded is a simulated event. The host registers it only when <c>Payments:Simulator:Enabled</c> is
/// true, and startup refuses that setting outside Development and Testing.
/// </summary>
public sealed class SimulatedPaymentProvider : IPaymentProvider
{
    public const string ProviderName = "Simulator";

    public string Name => ProviderName;
    public bool IsSimulated => true;
    public bool IsAvailable => true;

    /// <summary>
    /// The reference is 256 random bits, handed only to the order's owner. Whoever reports an outcome must present it
    /// together with the matching order and attempt IDs (see PaymentService.CompleteAsync).
    /// </summary>
    public Task<ProviderSession> InitiateAsync(PaymentInitiation initiation, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderSession("sim_" + Base64Url(RandomNumberGenerator.GetBytes(32))));

    /// <summary>The simulator is stateless: a payment it was asked about is Pending (verdicts only arrive via the callback), one that was never initiated is NotFound.</summary>
    public Task<ProviderPaymentState> GetPaymentStatusAsync(Guid attemptId, string? providerReference, CancellationToken cancellationToken = default) =>
        Task.FromResult(providerReference is null ? ProviderPaymentState.NotFound : ProviderPaymentState.Pending);

    /// <summary>SIMULATED refund: succeeds immediately, deterministically per idempotency key, and moves no money.</summary>
    public Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderRefundResult(true, true, "simref_" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.IdempotencyKey)))[..24].ToLowerInvariant(), null, false));

    /// <summary>Stateless: it cannot remember submissions, so asking always answers NotFound and a re-submission is harmless (same id, no money).</summary>
    public Task<ProviderRefundState> GetRefundStatusAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(ProviderRefundState.NotFound);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The production default until a real provider adapter exists: payments cannot be started.</summary>
public sealed class UnavailablePaymentProvider : IPaymentProvider
{
    public string Name => "None";
    public bool IsSimulated => false;
    public bool IsAvailable => false;

    public Task<ProviderSession> InitiateAsync(PaymentInitiation initiation, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No payment provider is configured.");

    public Task<ProviderPaymentState> GetPaymentStatusAsync(Guid attemptId, string? providerReference, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No payment provider is configured.");

    public Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No payment provider is configured.");

    public Task<ProviderRefundState> GetRefundStatusAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No payment provider is configured.");
}

/// <summary>Production default until a real notification adapter exists: deliveries are retried, then flagged for review.</summary>
public sealed class UnavailableNotificationSender : INotificationSender
{
    public string Channel => "None";
    public bool IsAvailable => false;

    public Task SendAsync(NotificationRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No notification adapter is configured.");
}
