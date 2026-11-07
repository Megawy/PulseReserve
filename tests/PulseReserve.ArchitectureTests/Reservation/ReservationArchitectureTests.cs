using FluentAssertions;
using NetArchTest.Rules;
using PulseReserve.Modules.Reservation;
using Xunit;

namespace PulseReserve.ArchitectureTests.Reservation;

public class ReservationArchitectureTests
{
    private static readonly System.Reflection.Assembly ReservationAssembly = typeof(ReservationModule).Assembly;

    private static readonly string[] DatabaseNamespaces = ["Microsoft.EntityFrameworkCore", "Npgsql"];

    [Theory]
    [InlineData("PulseReserve.Modules.Reservation.Domain")]
    [InlineData("PulseReserve.Modules.Reservation.Features")]
    public void ReservationDomainAndFeatures_MustNotReference_DatabaseLibraries(string layerNamespace)
    {
        var result = Types.InAssembly(ReservationAssembly)
            .That().ResideInNamespace(layerNamespace)
            .ShouldNot().HaveDependencyOnAny(DatabaseNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"{layerNamespace} must stay persistence-ignorant: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void ReservationPersistenceImplementation_MustResideIn_PersistencePostgresNamespace()
    {
        // ADR-008: module-owned persistence. Only Persistence.Postgres (and the DI registration) may touch EF Core/Npgsql.
        var result = Types.InAssembly(ReservationAssembly)
            .That().HaveDependencyOnAny(DatabaseNamespaces)
            .Should().ResideInNamespace("PulseReserve.Modules.Reservation.Persistence.Postgres")
            .Or().HaveName(nameof(ReservationModuleExtensions))
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Database access must stay in Persistence.Postgres: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Reservation_MustNotReference_AnyCatalogNamespace()
    {
        // Reservation reads catalog data only through its own IEventSalesCatalog port, adapted in the composition root.
        var result = Types.InAssembly(ReservationAssembly)
            .ShouldNot().HaveDependencyOn("PulseReserve.Modules.Catalog")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }
}
