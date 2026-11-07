namespace PulseReserve.Modules.Payments.Features;

internal static class CancellationRules
{
    /// <summary>
    /// True only when the CALLER asked to stop (the request was aborted or the host is shutting down). An <see cref="HttpClient"/>
    /// whose timeout elapses throws <see cref="TaskCanceledException"/> - an <see cref="OperationCanceledException"/> - although nobody
    /// cancelled anything, and that is exactly the "the provider did not answer" case the payment code must treat as AMBIGUOUS (the
    /// provider may or may not have acted), never as a cancellation that escapes as a 500.
    /// </summary>
    public static bool IsCallerCancellation(Exception exception, CancellationToken callerToken) =>
        exception is OperationCanceledException && callerToken.IsCancellationRequested;
}
