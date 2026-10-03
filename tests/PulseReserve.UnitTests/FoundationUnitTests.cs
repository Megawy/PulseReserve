using FluentAssertions;
using PulseReserve.Infrastructure;
using PulseReserve.SharedKernel;
using Xunit;

namespace PulseReserve.UnitTests;

public class FoundationUnitTests
{
    [Fact]
    public void SystemDateTimeProvider_ShouldReturnValidUtcNow()
    {
        // Arrange
        IDateTimeProvider provider = new SystemDateTimeProvider();

        // Act
        var now = provider.UtcNow;

        // Assert
        now.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }
}
