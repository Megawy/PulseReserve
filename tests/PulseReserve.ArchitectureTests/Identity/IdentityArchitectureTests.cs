using FluentAssertions;
using NetArchTest.Rules;
using PulseReserve.Modules.Identity;
using Xunit;

namespace PulseReserve.ArchitectureTests.Identity;

public class IdentityArchitectureTests
{
    private static readonly System.Reflection.Assembly IdentityAssembly = typeof(IdentityModule).Assembly;

    private static readonly string[] DatabaseNamespaces = ["Microsoft.EntityFrameworkCore", "Npgsql"];

    [Theory]
    [InlineData("PulseReserve.Modules.Identity.Domain")]
    [InlineData("PulseReserve.Modules.Identity.Features")]
    public void IdentityDomainAndFeatures_MustNotReference_DatabaseLibraries(string layerNamespace)
    {
        var result = Types.InAssembly(IdentityAssembly)
            .That().ResideInNamespace(layerNamespace)
            .ShouldNot().HaveDependencyOnAny(DatabaseNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"{layerNamespace} must stay persistence-ignorant: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void IdentityPersistenceImplementation_MustResideIn_PersistencePostgresNamespace()
    {
        // ADR-008: module-owned persistence. Only Persistence.Postgres (and the DI registration) may touch EF Core/Npgsql.
        var result = Types.InAssembly(IdentityAssembly)
            .That().HaveDependencyOnAny(DatabaseNamespaces)
            .Should().ResideInNamespace("PulseReserve.Modules.Identity.Persistence.Postgres")
            .Or().HaveName(nameof(IdentityModuleExtensions))
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Database access must stay in Persistence.Postgres: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Identity_MustNotReference_AnyOtherModule()
    {
        // Identity stands alone: the host derives ownership from the session, no module reads another module.
        var result = Types.InAssembly(IdentityAssembly)
            .ShouldNot().HaveDependencyOnAny("PulseReserve.Modules.Catalog", "PulseReserve.Modules.Reservation")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }
}
