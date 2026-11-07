using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetArchTest.Rules;
using PulseReserve.Infrastructure.Persistence.Catalog;
using Xunit;

namespace PulseReserve.ArchitectureTests.Catalog;

public class CatalogPersistenceArchitectureTests
{
    private static readonly System.Reflection.Assembly InfrastructureAssembly =
        typeof(CatalogDbContext).Assembly;

    private static readonly System.Reflection.Assembly CatalogAssembly =
        typeof(PulseReserve.Modules.Catalog.CatalogModule).Assembly;

    private static readonly System.Reflection.Assembly CatalogContractsAssembly =
        typeof(PulseReserve.Modules.Catalog.Contracts.ICatalogContractsMarker).Assembly;

    [Fact]
    public void CatalogDbContext_MustResideIn_InfrastructurePersistenceCatalogNamespace()
    {
        typeof(CatalogDbContext).Namespace.Should().Be("PulseReserve.Infrastructure.Persistence.Catalog");
    }

    [Fact]
    public void CatalogDbContext_MustInheritFrom_DbContext()
    {
        typeof(CatalogDbContext).Should().BeAssignableTo<DbContext>();
    }

    [Fact]
    public void CatalogDbContext_MustConfigure_CatalogSchema_AsDefault()
    {
        // Arrange: configure options with Npgsql provider (no live connection needed to build in-memory metadata model)
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=test_catalog_db")
            .Options;

        // Act
        using var context = new CatalogDbContext(options);

        // Assert
        context.Model.GetDefaultSchema().Should().Be(CatalogDbContext.Schema);
        CatalogDbContext.Schema.Should().Be("catalog");
    }

    [Fact]
    public void CatalogDomain_MustNotReference_Infrastructure_Or_EntityFrameworkCore()
    {
        var forbiddenDependencies = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "PulseReserve.Infrastructure"
        };

        var result = Types.InAssembly(CatalogAssembly)
            .That()
            .ResideInNamespace("PulseReserve.Modules.Catalog.Domain")
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenDependencies)
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Catalog domain must remain pure and free from EF Core and Infrastructure.");
    }

    [Fact]
    public void CatalogContracts_MustNotReference_Infrastructure_Or_EntityFrameworkCore()
    {
        var forbiddenDependencies = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "PulseReserve.Infrastructure"
        };

        var result = Types.InAssembly(CatalogContractsAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenDependencies)
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Catalog contracts must remain completely free from persistence dependencies.");
    }

    [Fact]
    public void Infrastructure_MustReference_CatalogDomain_ForPersistenceMapping()
    {
        var referencedAssemblies = InfrastructureAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        referencedAssemblies.Should().Contain("PulseReserve.Modules.Catalog",
            "Infrastructure must reference Catalog domain to implement persistence mappings.");
    }
}
