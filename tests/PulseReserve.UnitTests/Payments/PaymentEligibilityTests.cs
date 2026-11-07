using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// Per-ORDER payment eligibility (separate from provider capabilities). When an order cannot be paid the options endpoint says so
/// with a customer-safe reason and offers no method at all, instead of listing methods that would be refused.
/// </summary>
public sealed class PaymentEligibilityTests
{
    private static (PaymentService Service, InMemoryOrderStore Store) Build(params string[] providers)
    {
        var store = new InMemoryOrderStore("USD", 91m);
        var registry = new PaymentProviderRegistry((providers.Length == 0 ? ["Stripe", "Paymob"] : providers).Select(n => (IPaymentProvider)new ScriptedProvider(n, false, "USD")).ToList());
        return (new PaymentService(store, registry, new ConfigurableGateway()), store);
    }

    [Fact]
    public async Task APayableOrder_IsEligible_AndListsEveryProviderThatCanChargeIt()
    {
        var (service, store) = Build();

        var options = (await service.OptionsAsync(store.CustomerId, store.Order.OrderId)).Value;

        options.Eligible.Should().BeTrue();
        options.Reason.Should().BeNull();
        options.Providers.Select(p => p.Name).Should().BeEquivalentTo("Stripe", "Paymob");
    }

    [Fact]
    public async Task AnOrderWithAPaymentException_IsHeld_ForEveryProvider_AndOffersNoMethod()
    {
        var (service, store) = Build();
        store.Exceptions.Add((Guid.NewGuid(), "evt_1"));

        var options = (await service.OptionsAsync(store.CustomerId, store.Order.OrderId)).Value;

        options.Eligible.Should().BeFalse();
        options.Reason.Should().Be(PaymentIneligibility.OrderHeldForReview);
        options.Providers.Should().BeEmpty("no method may be offered that the server would refuse, whichever provider it is");
    }

    [Theory]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.RefundRequired)]
    [InlineData(OrderStatus.Refunded)]
    public async Task AnOrderThatIsNoLongerAwaitingPayment_IsNotPayable(string status)
    {
        var (service, store) = Build();
        store.Order = store.Order with { Status = status };

        var options = (await service.OptionsAsync(store.CustomerId, store.Order.OrderId)).Value;

        (options.Eligible, options.Reason, options.Providers.Count).Should().Be((false, PaymentIneligibility.OrderNotPayable, 0));
    }

    [Fact]
    public async Task AnOrderPastItsPaymentWindow_IsNotPayable()
    {
        var (service, store) = Build();
        store.Order = store.Order with { PaymentDeadlineUtc = DateTime.UtcNow.AddMinutes(-1) };

        (await service.OptionsAsync(store.CustomerId, store.Order.OrderId)).Value.Reason.Should().Be(PaymentIneligibility.OrderNotPayable);
    }

    [Fact]
    public async Task APayableOrderWhoseCurrencyNoProviderCanCharge_SaysSo_RatherThanListingNothing()
    {
        var store = new InMemoryOrderStore("EUR", 91m);
        var service = new PaymentService(store, new PaymentProviderRegistry([new ScriptedProvider("Stripe", false, "USD")]), new ConfigurableGateway());

        var options = (await service.OptionsAsync(store.CustomerId, store.Order.OrderId)).Value;

        (options.Eligible, options.Reason).Should().Be((false, PaymentIneligibility.NoPaymentMethod));
    }

    [Fact]
    public async Task AnotherCustomersOrder_IsNotFound_NotAnEligibilityAnswer()
    {
        var (service, store) = Build();

        (await service.OptionsAsync(Guid.NewGuid(), store.Order.OrderId)).Error.Should().Be(PaymentsErrors.OrderNotFound);
    }

    [Fact]
    public async Task PayingAHeldOrder_IsRefused_ConsistentlyWithTheOptions()
    {
        var (service, store) = Build();
        store.Exceptions.Add((Guid.NewGuid(), null));

        foreach (var provider in new[] { "Stripe", "Paymob" })
            (await service.InitiateAsync(store.CustomerId, store.Order.OrderId, "k-" + provider, provider)).Error.Should().Be(PaymentsErrors.OrderHeldForReview);
    }

    [Fact]
    public async Task TheEndpoint_ExposesEligibilityAndAReasonOnly_NeverAnAdministratorDetail()
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true);
        host.Store.Exceptions.Add((Guid.NewGuid(), "evt_secretish_1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{host.Store.Order.OrderId}/payment-options");
        request.Headers.Add("X-Test-User", host.CustomerId.ToString());

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("eligible").GetBoolean().Should().BeFalse();
        json.GetProperty("reason").GetString().Should().Be("OrderHeldForReview");
        json.GetProperty("providers").GetArrayLength().Should().Be(0);
        var body = json.ToString().ToLowerInvariant();
        body.Should().NotContain("exception").And.NotContain("evt_").And.NotContain("attempt").And.NotContain("administrator");
    }

    [Fact]
    public async Task TheEndpoint_ForAPayableOrder_IsEligible_AndSeparateFromProviderCapabilities()
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{host.Store.Order.OrderId}/payment-options");
        request.Headers.Add("X-Test-User", host.CustomerId.ToString());

        using var response = await host.Client.SendAsync(request);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("eligible").GetBoolean().Should().BeTrue();
        json.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("provider").GetString()).Should().BeEquivalentTo("Stripe", "Paymob");
        json.GetRawText().ToLowerInvariant().Should().NotContain("supportsrefunds").And.NotContain("supportspaymentlookup").And.NotContain("idempotent");
    }
}
