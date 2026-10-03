using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace PulseReserve.ArchitectureTests.Modules;

public class ModuleBoundaryTests
{
    private static readonly string[] AllModuleInternalNamespaces =
    [
        "PulseReserve.Modules.Identity",
        "PulseReserve.Modules.Catalog",
        "PulseReserve.Modules.Reservation",
        "PulseReserve.Modules.Payments",
        "PulseReserve.Modules.Notifications"
    ];

    [Theory]
    [InlineData("Identity", "PulseReserve.Modules.Identity")]
    [InlineData("Catalog", "PulseReserve.Modules.Catalog")]
    [InlineData("Reservation", "PulseReserve.Modules.Reservation")]
    [InlineData("Payments", "PulseReserve.Modules.Payments")]
    [InlineData("Notifications", "PulseReserve.Modules.Notifications")]
    public void Module_MustNotDependOn_OtherModuleInternalImplementations(string moduleName, string moduleNamespace)
    {
        // Arrange: find all other module internal namespaces except the current module
        var forbiddenNamespaces = AllModuleInternalNamespaces
            .Where(ns => !ns.Equals(moduleNamespace, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var assembly = GetModuleAssembly(moduleName);

        // Act
        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        // Assert
        result.IsSuccessful.Should().BeTrue(
            $"Module '{moduleName}' must not reference internal implementations of other modules: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Theory]
    [InlineData("Identity")]
    [InlineData("Catalog")]
    [InlineData("Reservation")]
    [InlineData("Payments")]
    [InlineData("Notifications")]
    public void Contracts_MustNotDependOn_InternalModuleImplementations_Or_Infrastructure(string moduleName)
    {
        // Arrange: Contracts must not reference internal assemblies or infrastructure
        var contractsAssembly = GetContractsAssembly(moduleName);
        var referencedAssemblies = contractsAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        // Assert: assembly reference check
        referencedAssemblies.Should().NotContain([
            $"PulseReserve.Modules.{moduleName}",
            "PulseReserve.Infrastructure",
            "PulseReserve.AppHost"
        ]);

        // NetArchTest check: types in Contracts must not depend on internal domain or infrastructure
        var internalSubNamespaces = new[]
        {
            $"PulseReserve.Modules.{moduleName}.Domain",
            "PulseReserve.Infrastructure",
            "PulseReserve.AppHost"
        };

        var result = Types.InAssembly(contractsAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(internalSubNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Contracts for '{moduleName}' must remain lightweight: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void SharedKernel_MustNotDependOn_Infrastructure_AppHost_Or_Modules()
    {
        // Arrange
        var sharedKernelAssembly = typeof(PulseReserve.SharedKernel.IDateTimeProvider).Assembly;
        var forbiddenNamespaces = AllModuleInternalNamespaces
            .Concat(["PulseReserve.Infrastructure", "PulseReserve.AppHost"])
            .ToArray();

        // Act
        var result = Types.InAssembly(sharedKernelAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        // Assert
        result.IsSuccessful.Should().BeTrue(
            $"SharedKernel must not have external dependencies on Infrastructure, AppHost, or Modules: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void SharedKernel_And_Contracts_MustNotDependOn_DatabaseOrMessagingLibraries()
    {
        // Arrange
        var forbiddenDependencies = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "StackExchange.Redis",
            "Confluent.Kafka",
            "MassTransit"
        };

        var assemblies = new[]
        {
            typeof(PulseReserve.SharedKernel.IDateTimeProvider).Assembly,
            typeof(PulseReserve.Modules.Identity.Contracts.IIdentityContractsMarker).Assembly,
            typeof(PulseReserve.Modules.Catalog.Contracts.ICatalogContractsMarker).Assembly,
            typeof(PulseReserve.Modules.Reservation.Contracts.IReservationContractsMarker).Assembly,
            typeof(PulseReserve.Modules.Payments.Contracts.IPaymentsContractsMarker).Assembly,
            typeof(PulseReserve.Modules.Notifications.Contracts.INotificationsContractsMarker).Assembly
        };

        // Act & Assert
        foreach (var assembly in assemblies)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(forbiddenDependencies)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"Assembly '{assembly.GetName().Name}' must not reference database or messaging libraries: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact]
    public void AppHost_MayReference_ModuleImplementations_And_Infrastructure()
    {
        // Arrange
        var appHostAssembly = typeof(Program).Assembly;
        var referencedAssemblies = appHostAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        // Assert: AppHost is the composition root and must reference all modules and infrastructure
        referencedAssemblies.Should().Contain([
            "PulseReserve.Modules.Identity",
            "PulseReserve.Modules.Catalog",
            "PulseReserve.Modules.Reservation",
            "PulseReserve.Modules.Payments",
            "PulseReserve.Modules.Notifications",
            "PulseReserve.Infrastructure",
            "PulseReserve.SharedKernel"
        ]);
    }

    private static System.Reflection.Assembly GetModuleAssembly(string moduleName) => moduleName switch
    {
        "Identity" => typeof(PulseReserve.Modules.Identity.IdentityModule).Assembly,
        "Catalog" => typeof(PulseReserve.Modules.Catalog.CatalogModule).Assembly,
        "Reservation" => typeof(PulseReserve.Modules.Reservation.ReservationModule).Assembly,
        "Payments" => typeof(PulseReserve.Modules.Payments.PaymentsModule).Assembly,
        "Notifications" => typeof(PulseReserve.Modules.Notifications.NotificationsModule).Assembly,
        _ => throw new ArgumentException($"Unknown module: {moduleName}", nameof(moduleName))
    };

    private static System.Reflection.Assembly GetContractsAssembly(string moduleName) => moduleName switch
    {
        "Identity" => typeof(PulseReserve.Modules.Identity.Contracts.IIdentityContractsMarker).Assembly,
        "Catalog" => typeof(PulseReserve.Modules.Catalog.Contracts.ICatalogContractsMarker).Assembly,
        "Reservation" => typeof(PulseReserve.Modules.Reservation.Contracts.IReservationContractsMarker).Assembly,
        "Payments" => typeof(PulseReserve.Modules.Payments.Contracts.IPaymentsContractsMarker).Assembly,
        "Notifications" => typeof(PulseReserve.Modules.Notifications.Contracts.INotificationsContractsMarker).Assembly,
        _ => throw new ArgumentException($"Unknown module: {moduleName}", nameof(moduleName))
    };
}
