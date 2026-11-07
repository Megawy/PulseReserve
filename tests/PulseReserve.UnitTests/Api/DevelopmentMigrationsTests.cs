using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using PulseReserve.AppHost.Composition;
using Xunit;

namespace PulseReserve.UnitTests.Api;

public class DevelopmentMigrationsTests
{
    private static WebApplication AppWith(string environment, string? migrate)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        if (migrate is not null)
            builder.Configuration[DevelopmentMigrations.ConfigKey] = migrate;
        return builder.Build();
    }

    [Fact]
    public async Task NotRequested_DoesNothing_EvenWithoutADatabase()
    {
        await using var app = AppWith(Environments.Production, migrate: null);

        await DevelopmentMigrations.ApplyIfRequestedAsync(app); // must not throw or touch services
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Requested_OutsideDevelopment_IsRefused(string environment)
    {
        await using var app = AppWith(environment, migrate: "true");

        var act = () => DevelopmentMigrations.ApplyIfRequestedAsync(app);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*only honoured in the Development*");
    }
}
