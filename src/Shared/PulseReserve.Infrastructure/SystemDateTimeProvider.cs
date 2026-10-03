using PulseReserve.SharedKernel;

namespace PulseReserve.Infrastructure;

/// <summary>
/// Minimal placeholder implementation of IDateTimeProvider.
/// </summary>
public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
