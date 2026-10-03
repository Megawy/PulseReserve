namespace PulseReserve.SharedKernel;

/// <summary>
/// Minimal foundational abstraction for time abstraction across modules.
/// </summary>
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}
