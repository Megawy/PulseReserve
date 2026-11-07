using FluentAssertions;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

// These tests need neither Docker nor a database: the hosts point at 127.0.0.1 ports 1/2 (nothing listens there)
// and host construction performs no database I/O. No test omits the configured connection string, because the
// resolver would then fall back to PULSERESERVE_TEST_DATABASE (covered with an injected fake in CatalogConnectionStringTests).
public class TestDatabaseGuardTests
{
    private const string Expected = "Host=127.0.0.1;Port=1;Database=guard_expected;Username=guard;Password=expected-secret";
    private const string OtherPort = "Host=127.0.0.1;Port=2;Database=guard_expected;Username=guard;Password=other-secret";
    private const string OtherDatabase = "Host=127.0.0.1;Port=1;Database=guard_other;Username=guard;Password=expected-secret";
    private const string OtherHost = "Host=192.0.2.1;Port=1;Database=guard_expected;Username=guard;Password=expected-secret";
    private const string OtherUser = "Host=127.0.0.1;Port=1;Database=guard_expected;Username=intruder;Password=expected-secret";

    [Fact]
    public void Guard_Passes_WhenHostPortDatabaseAndUserMatch()
    {
        var act = () => TestDatabaseGuard.EnsureTargetsExpectedDatabase(Expected, Expected);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(OtherPort)]
    [InlineData(OtherDatabase)]
    [InlineData(OtherHost)]
    [InlineData(OtherUser)]
    public void Guard_Throws_WhenAnyIdentityPartDiffers(string resolved)
    {
        var act = () => TestDatabaseGuard.EnsureTargetsExpectedDatabase(resolved, Expected);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Test database guard]*")
            .Which.Message.Should().NotContain("secret");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Guard_Throws_WhenNothingWasResolved(string? resolved)
    {
        var act = () => TestDatabaseGuard.EnsureTargetsExpectedDatabase(resolved, Expected);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Test database guard]*");
    }

    [Fact]
    public void Factory_RefusesToCreateClient_WhenHostResolvesAnUnexpectedDatabase()
    {
        using var factory = new CatalogApiFactory(Expected, configuredConnectionString: OtherPort);

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Test database guard]*No HTTP client was created*");
    }

    [Fact]
    public void Factory_RefusesToExposeServices_WhenHostResolvesAnUnexpectedDatabase()
    {
        using var factory = new CatalogApiFactory(Expected, configuredConnectionString: OtherDatabase);

        var act = () => factory.Services;

        act.Should().Throw<InvalidOperationException>().WithMessage("*Test database guard]*");
    }

    [Fact]
    public void Factory_CreatesClient_WhenHostResolvesTheExpectedDatabase()
    {
        // Configuration wins over the legacy environment variable, so the host resolves exactly the configured database.
        using var factory = new CatalogApiFactory(Expected, configuredConnectionString: Expected);

        using var client = factory.CreateClient();

        client.Should().NotBeNull();
    }
}
