using FluentAssertions;
using NetArchTest.Rules;
using PulseReserve.Infrastructure.Persistence.Catalog;
using PulseReserve.Modules.Catalog.Persistence;
using Xunit;

namespace PulseReserve.ArchitectureTests.Catalog;

public class CatalogApplicationArchitectureTests
{
    private static readonly System.Reflection.Assembly CatalogAssembly =
        typeof(PulseReserve.Modules.Catalog.CatalogModule).Assembly;

    private static readonly System.Reflection.Assembly InfrastructureAssembly =
        typeof(CatalogDbContext).Assembly;

    private static readonly System.Reflection.Assembly CatalogContractsAssembly =
        typeof(PulseReserve.Modules.Catalog.Contracts.ICatalogContractsMarker).Assembly;

    [Fact]
    public void CatalogApplication_MustNotReference_EntityFrameworkCore()
    {
        var forbiddenDependencies = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Microsoft.EntityFrameworkCore.Relational",
            "Npgsql",
            "Npgsql.EntityFrameworkCore.PostgreSQL"
        };

        var result = Types.InAssembly(CatalogAssembly)
            .That()
            .ResideInNamespace("PulseReserve.Modules.Catalog.Features")
            .Or()
            .ResideInNamespace("PulseReserve.Modules.Catalog.Persistence")
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenDependencies)
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Catalog application and persistence abstractions must be completely free from EF Core and Npgsql.");
    }

    [Fact]
    public void CatalogApplication_MustNotReference_Infrastructure()
    {
        var result = Types.InAssembly(CatalogAssembly)
            .ShouldNot()
            .HaveDependencyOn("PulseReserve.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Catalog module must have zero knowledge of Infrastructure implementation.");
    }

    [Fact]
    public void GenericRepositoriesOrUnitOfWork_MustNotExist()
    {
        var genericRepoPattern = Types.InAssembly(CatalogAssembly)
            .That()
            .AreInterfaces()
            .And()
            .HaveNameMatching(".*Repository`[0-9]+.*")
            .GetTypes();

        genericRepoPattern.Should().BeEmpty("Generic repository abstractions (e.g. IRepository<T>) are strictly forbidden.");

        var genericUow = Types.InAssembly(CatalogAssembly)
            .That()
            .AreInterfaces()
            .And()
            .HaveName("IUnitOfWork")
            .GetTypes();

        genericUow.Should().BeEmpty("Generic IUnitOfWork is strictly forbidden; only bounded-context specific ICatalogUnitOfWork is permitted.");
    }

    [Fact]
    public void CatalogPersistenceImplementations_MustResideIn_Infrastructure()
    {
        var repoImplementations = new[]
        {
            typeof(PulseReserve.Infrastructure.Persistence.Catalog.Repositories.VenueRepository),
            typeof(PulseReserve.Infrastructure.Persistence.Catalog.Repositories.SeatingLayoutRepository),
            typeof(PulseReserve.Infrastructure.Persistence.Catalog.Repositories.EventRepository),
            typeof(PulseReserve.Infrastructure.Persistence.Catalog.Repositories.CatalogUnitOfWork)
        };

        foreach (var impl in repoImplementations)
        {
            impl.Assembly.FullName.Should().Be(InfrastructureAssembly.FullName,
                $"{impl.Name} persistence implementation must reside strictly in Infrastructure.");
            impl.Namespace.Should().StartWith("PulseReserve.Infrastructure.Persistence.Catalog",
                $"{impl.Name} must reside in the Catalog persistence namespace inside Infrastructure.");
        }

        typeof(IVenueRepository).IsAssignableFrom(typeof(PulseReserve.Infrastructure.Persistence.Catalog.Repositories.VenueRepository)).Should().BeTrue();
        typeof(ISeatingLayoutRepository).IsAssignableFrom(typeof(PulseReserve.Infrastructure.Persistence.Catalog.Repositories.SeatingLayoutRepository)).Should().BeTrue();
        typeof(IEventRepository).IsAssignableFrom(typeof(PulseReserve.Infrastructure.Persistence.Catalog.Repositories.EventRepository)).Should().BeTrue();
        typeof(ICatalogUnitOfWork).IsAssignableFrom(typeof(PulseReserve.Infrastructure.Persistence.Catalog.Repositories.CatalogUnitOfWork)).Should().BeTrue();
    }

    [Fact]
    public void CatalogApplication_MustNotReference_ExternalModuleImplementations()
    {
        var forbiddenDependencies = new[]
        {
            "PulseReserve.Modules.Reservation",
            "PulseReserve.Modules.Payments",
            "PulseReserve.Modules.Notifications",
            "PulseReserve.Modules.Identity"
        };

        var result = Types.InAssembly(CatalogAssembly)
            .That()
            .ResideInNamespace("PulseReserve.Modules.Catalog.Features")
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenDependencies)
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Catalog features must not reference internal implementations of other modules.");
    }
}
