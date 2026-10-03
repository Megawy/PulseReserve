using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace PulseReserve.ArchitectureTests.Catalog;

public class CatalogArchitectureTests
{
    private static readonly System.Reflection.Assembly CatalogAssembly =
        typeof(PulseReserve.Modules.Catalog.CatalogModule).Assembly;

    private static readonly System.Reflection.Assembly CatalogContractsAssembly =
        typeof(PulseReserve.Modules.Catalog.Contracts.ICatalogContractsMarker).Assembly;

    private static readonly System.Reflection.Assembly ReservationAssembly =
        typeof(PulseReserve.Modules.Reservation.ReservationModule).Assembly;

    [Fact]
    public void Catalog_MustNotReference_ReservationInternalImplementation()
    {
        var result = Types.InAssembly(CatalogAssembly)
            .ShouldNot()
            .HaveDependencyOn("PulseReserve.Modules.Reservation")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Catalog must have zero knowledge of Reservation internal implementation.");
    }

    [Fact]
    public void Catalog_MustNotReference_PaymentsInternalImplementation()
    {
        var result = Types.InAssembly(CatalogAssembly)
            .ShouldNot()
            .HaveDependencyOn("PulseReserve.Modules.Payments")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Catalog must have zero knowledge of Payments internal implementation.");
    }

    [Fact]
    public void Catalog_MustNotReference_NotificationsInternalImplementation()
    {
        var result = Types.InAssembly(CatalogAssembly)
            .ShouldNot()
            .HaveDependencyOn("PulseReserve.Modules.Notifications")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Catalog must have zero knowledge of Notifications internal implementation.");
    }

    [Fact]
    public void CatalogDomain_MustNotReference_InfrastructureOrDatabasePackages()
    {
        var forbiddenDependencies = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "StackExchange.Redis",
            "Confluent.Kafka",
            "MassTransit"
        };

        var result = Types.InAssembly(CatalogAssembly)
            .That()
            .ResideInNamespace("PulseReserve.Modules.Catalog.Domain")
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenDependencies)
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Catalog domain models must remain pure with no database or messaging dependencies.");
    }

    [Fact]
    public void CatalogContracts_MustNotReference_CatalogImplementation()
    {
        var referencedAssemblies = CatalogContractsAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        referencedAssemblies.Should().NotContain("PulseReserve.Modules.Catalog");

        var result = Types.InAssembly(CatalogContractsAssembly)
            .ShouldNot()
            .HaveDependencyOn("PulseReserve.Modules.Catalog.Domain")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Catalog.Contracts must never reference internal Catalog implementations.");
    }

    [Fact]
    public void CatalogContracts_MustNotReference_DatabaseOrMessagingPackages()
    {
        var forbiddenDependencies = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "StackExchange.Redis",
            "Confluent.Kafka",
            "MassTransit"
        };

        var result = Types.InAssembly(CatalogContractsAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenDependencies)
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Catalog.Contracts must remain lightweight and free of infrastructure libraries.");
    }

    [Fact]
    public void Reservation_MustNotReference_CatalogInternalImplementation()
    {
        var result = Types.InAssembly(ReservationAssembly)
            .ShouldNot()
            .HaveDependencyOn("PulseReserve.Modules.Catalog")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Reservation must not reference Catalog internal classes.");
    }

    [Fact]
    public void CrossModuleReferences_MustRemainContractsOnly()
    {
        // Assert: Catalog referenced assemblies must NOT include any other module internal implementation
        var referencedAssemblies = CatalogAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        referencedAssemblies.Should().NotContain([
            "PulseReserve.Modules.Reservation",
            "PulseReserve.Modules.Payments",
            "PulseReserve.Modules.Notifications",
            "PulseReserve.Modules.Identity"
        ]);
    }

    [Fact]
    public void Event_MustNotDependOn_SeatingLayoutImplementation()
    {
        var result = Types.InAssembly(CatalogAssembly)
            .That()
            .ResideInNamespace("PulseReserve.Modules.Catalog.Domain.Events")
            .ShouldNot()
            .HaveDependencyOn("PulseReserve.Modules.Catalog.Domain.SeatingLayouts")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Event aggregate must not directly reference or inspect SeatingLayout aggregate implementation.");
    }

    [Fact]
    public void SeatingLayout_MustNotDependOn_EventImplementation()
    {
        var result = Types.InAssembly(CatalogAssembly)
            .That()
            .ResideInNamespace("PulseReserve.Modules.Catalog.Domain.SeatingLayouts")
            .ShouldNot()
            .HaveDependencyOn("PulseReserve.Modules.Catalog.Domain.Events")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("SeatingLayout aggregate must have zero knowledge of Event aggregate implementation.");
    }
}
