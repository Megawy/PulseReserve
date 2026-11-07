using FluentAssertions;
using NetArchTest.Rules;
using PulseReserve.Modules.Payments;
using Xunit;

namespace PulseReserve.ArchitectureTests.Payments;

public class PaymentsArchitectureTests
{
    private static readonly System.Reflection.Assembly PaymentsAssembly = typeof(PaymentsModule).Assembly;

    private static readonly string[] DatabaseNamespaces = ["Microsoft.EntityFrameworkCore", "Npgsql"];

    [Theory]
    [InlineData("PulseReserve.Modules.Payments.Domain")]
    [InlineData("PulseReserve.Modules.Payments.Features")]
    public void PaymentsDomainAndFeatures_MustNotReference_DatabaseLibraries(string layerNamespace)
    {
        var result = Types.InAssembly(PaymentsAssembly)
            .That().ResideInNamespace(layerNamespace)
            .ShouldNot().HaveDependencyOnAny(DatabaseNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"{layerNamespace} must stay persistence-ignorant: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void PaymentsPersistenceImplementation_MustResideIn_PersistencePostgresNamespace()
    {
        // ADR-008: module-owned persistence. Only Persistence.Postgres (and the DI registration) may touch EF Core/Npgsql.
        var result = Types.InAssembly(PaymentsAssembly)
            .That().HaveDependencyOnAny(DatabaseNamespaces)
            .Should().ResideInNamespace("PulseReserve.Modules.Payments.Persistence.Postgres")
            .Or().HaveName(nameof(PaymentsModuleExtensions))
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Database access must stay in Persistence.Postgres: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Payments_MustNotReference_AnyOtherModule()
    {
        // Payments reaches reservations only through its IReservationCheckoutGateway port, adapted in the composition root.
        var result = Types.InAssembly(PaymentsAssembly)
            .ShouldNot().HaveDependencyOnAny("PulseReserve.Modules.Catalog", "PulseReserve.Modules.Reservation", "PulseReserve.Modules.Identity")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void PaymentsFeaturesAndDomain_MustNotDependOnAnyConcreteProvider()
    {
        // The order domain talks to IPaymentProvider only; the simulator is a replaceable adapter.
        var result = Types.InAssembly(PaymentsAssembly)
            .That().ResideInNamespace("PulseReserve.Modules.Payments.Domain")
            .Or().ResideInNamespace("PulseReserve.Modules.Payments.Features")
            .ShouldNot().HaveDependencyOn("PulseReserve.Modules.Payments.Providers")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }
}
