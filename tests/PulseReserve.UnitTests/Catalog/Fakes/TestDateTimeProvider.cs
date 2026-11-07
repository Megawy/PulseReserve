using PulseReserve.SharedKernel;

namespace PulseReserve.UnitTests.Catalog.Fakes;

public sealed class TestDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
}
