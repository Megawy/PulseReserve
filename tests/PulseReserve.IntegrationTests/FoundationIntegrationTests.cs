using FluentAssertions;
using Xunit;

namespace PulseReserve.IntegrationTests;

public class FoundationIntegrationTests
{
    [Fact]
    public void AppHost_ProgramType_ShouldBeDiscoverable()
    {
        // Arrange & Act
        var programType = typeof(Program);

        // Assert
        programType.Should().NotBeNull();
        programType.Assembly.GetName().Name.Should().Be("PulseReserve.AppHost");
    }
}
