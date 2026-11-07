using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// The hosted-checkout link can embed a provider client secret. It is stored only while its attempt is still in flight, even
/// when the attempt is closed while the provider call is running. The in-memory store applies the same rule as the SQL; the SQL
/// itself (a single UPDATE evaluated on the latest committed row) is exercised only by the PostgreSQL tests.
/// </summary>
public sealed class CheckoutLinkHygieneTests
{
    [Theory]
    [InlineData(PaymentAttemptStatus.Cancelled)]
    [InlineData(PaymentAttemptStatus.Failed)]
    public async Task AnAttemptClosedDuringTheProviderCall_KeepsItsReference_ButNeverStoresOrShowsTheLink(string closedAs)
    {
        var store = new InMemoryOrderStore("USD", 91m);
        var provider = new ScriptedProvider("Stripe", false, "USD");
        provider.OnInitiate = i =>
        {
            store.Attempts.Single(a => a.Id == i.AttemptId).Status = closedAs; // closed by someone else while this call runs
            return new ProviderSession("cs_test_closed", "https://pay.example.test/secret-link");
        };
        var service = new PaymentService(store, new PaymentProviderRegistry([provider]), new ConfigurableGateway());

        var result = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, "k1", "Stripe");

        var row = store.Attempts.Single();
        row.Reference.Should().Be("cs_test_closed", "a later verified verdict must still be able to find the attempt");
        row.CheckoutUrl.Should().BeNull("the link is never written for an attempt that is no longer in flight");
        result.IsSuccess.Should().BeTrue();
        CheckoutUrlPolicy.Visible(store.Order.Status, result.Value.Attempt).Should().BeNull();
    }

    [Fact]
    public async Task AnAttemptStillInFlight_StoresAndShowsItsLink()
    {
        var store = new InMemoryOrderStore("USD", 91m);
        var provider = new ScriptedProvider("Stripe", false, "USD");
        var service = new PaymentService(store, new PaymentProviderRegistry([provider]), new ConfigurableGateway());

        var result = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, "k1", "Stripe");

        store.Attempts.Single().CheckoutUrl.Should().StartWith("https://pay.example.test/");
        CheckoutUrlPolicy.Visible(store.Order.Status, result.Value.Attempt).Should().NotBeNull();
    }
}
