using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PulseReserve.AppHost.Configuration;
using Xunit;

namespace PulseReserve.UnitTests.Api;

public class CatalogConnectionStringTests
{
    private const string Configured = "Host=configured;Database=catalog";
    private const string Legacy = "Host=legacy;Database=catalog";

    [Fact]
    public void ConfiguredConnectionString_WinsOverLegacyEnvironmentVariable()
    {
        var resolved = CatalogConnectionString.Resolve(ConfigWith(Configured), EnvWith(Legacy));

        resolved.Should().Be(Configured);
    }

    [Fact]
    public void LegacyEnvironmentVariable_IsUsedOnlyAsFallback()
    {
        var resolved = CatalogConnectionString.Resolve(ConfigWith(null), EnvWith(Legacy));

        resolved.Should().Be(Legacy);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", " ")]
    public void MissingConnectionString_Throws(string? configured, string? legacy)
    {
        var act = () => CatalogConnectionString.Resolve(ConfigWith(configured), EnvWith(legacy));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:Catalog*");
    }

    [Fact]
    public void OnlyTheLegacyVariableName_IsRead()
    {
        var requested = new List<string>();

        CatalogConnectionString.Resolve(ConfigWith(null), name =>
        {
            requested.Add(name);
            return Legacy;
        });

        requested.Should().Equal(CatalogConnectionString.LegacyEnvironmentVariable);
    }

    private static IConfiguration ConfigWith(string? catalogConnectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Catalog"] = catalogConnectionString
            })
            .Build();

    private static Func<string, string?> EnvWith(string? legacyValue) =>
        name => name == CatalogConnectionString.LegacyEnvironmentVariable ? legacyValue : null;
}
