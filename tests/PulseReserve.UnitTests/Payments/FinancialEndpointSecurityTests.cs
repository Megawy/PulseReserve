using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// A sweep over every financial route the host maps (customer orders and payments, payment operations, provider webhooks):
/// each one is discovered from the real endpoint table, so a route added later is covered without editing this file. It uses
/// the real policies, the real antiforgery filter and the real rate limit; the stores are fakes that fail loudly when called,
/// so "refused" also means "nothing ran". It proves HTTP-level authorization, antiforgery and limiting, not SQL.
/// </summary>
public sealed class FinancialEndpointSecurityTests
{
    private static readonly string[] Prefixes = ["/api/orders", "/api/admin/payments", "/api/webhooks"];

    private sealed record Route(string Pattern, string Method, string Policy, bool AllowsAnonymous);

    private static IReadOnlyList<Route> RoutesOf(PaymentHttpHost host) =>
        ((IEndpointRouteBuilder)host.App).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => Prefixes.Any(p => (e.RoutePattern.RawText ?? string.Empty).StartsWith(p, StringComparison.Ordinal)))
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
                var policy = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).LastOrDefault(p => p is not null) ?? string.Empty;
                var anonymous = e.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                return methods.Select(m => new Route(e.RoutePattern.RawText!, m, policy, anonymous));
            })
            .Where(r => r.Method != "OPTIONS" && r.Method != "HEAD")
            .ToList();

    private static string Concrete(string pattern) =>
        Regex.Replace(pattern, @"\{[^}]*\}", m => m.Value.Contains("guid", StringComparison.OrdinalIgnoreCase) ? Guid.NewGuid().ToString() : "stripe");

    private static HttpRequestMessage Request(Route route, string? user = null, bool admin = false)
    {
        var request = new HttpRequestMessage(new HttpMethod(route.Method), Concrete(route.Pattern));
        // A well-formed JSON body: endpoint filters (the antiforgery check) run AFTER parameter binding, so an unparsable
        // body would be answered 400 before the filter is reached. What is under test here is the token, not the binding.
        if (route.Method != "GET")
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user);
            if (admin)
                request.Headers.Add("X-Test-Admin", "1");
        }

        return request;
    }

    [Fact]
    public async Task TheDiscoveredRouteTable_IsTheExpectedOne_SoTheSweepCannotSilentlyCoverNothing()
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true);

        var routes = RoutesOf(host);

        routes.Count.Should().BeGreaterThan(20);
        routes.Should().Contain(r => r.Pattern.StartsWith("/api/admin/payments/refunds", StringComparison.Ordinal) && r.Method == "POST");
        routes.Should().Contain(r => r.Pattern.Contains("exceptions", StringComparison.Ordinal) && r.Method == "POST");
        routes.Should().Contain(r => r.Pattern == "/api/webhooks/{provider}" && r.Method == "POST");
    }

    [Fact]
    public async Task OnlyTheProviderWebhook_IsAnonymous_EveryOtherFinancialRouteDeclaresAPolicy()
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true);

        var routes = RoutesOf(host);

        routes.Where(r => r.AllowsAnonymous || r.Policy.Length == 0).Select(r => $"{r.Method} {r.Pattern}")
            .Should().BeEquivalentTo(["POST /api/webhooks/{provider}"], "a webhook is anonymous by nature and protected by its signature");
        routes.Where(r => r.Pattern.StartsWith("/api/admin/payments", StringComparison.Ordinal)).Should().OnlyContain(r => r.Policy == "Admin");
        routes.Where(r => r.Pattern.StartsWith("/api/orders", StringComparison.Ordinal)).Should().OnlyContain(r => r.Policy == "Customer");
    }

    [Fact]
    public async Task EveryNonWebhookRoute_RefusesAnAnonymousCaller_BeforeAnythingRuns()
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true);

        foreach (var route in RoutesOf(host).Where(r => !r.AllowsAnonymous))
        {
            using var response = await host.Client.SendAsync(Request(route));
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{route.Method} {route.Pattern}");
        }

        host.Workflow.Calls.Should().BeEmpty();
        host.Store.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task EveryAdminRoute_RefusesACustomer_AndAnAdminWithoutTheAntiforgeryToken_BeforeAnythingRuns()
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true);
        var customer = Guid.NewGuid().ToString();

        foreach (var route in RoutesOf(host).Where(r => r.Policy == "Admin"))
        {
            using var asCustomer = await host.Client.SendAsync(Request(route, customer));
            asCustomer.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{route.Method} {route.Pattern} as a customer");

            if (route.Method != "GET")
            {
                using var noToken = await host.Client.SendAsync(Request(route, customer, admin: true));
                noToken.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{route.Method} {route.Pattern} without an antiforgery token");
                (await noToken.Content.ReadAsStringAsync()).Should().Contain("Csrf.Invalid");
            }
        }

        host.Workflow.Calls.Should().BeEmpty("no administrator operation ran");
    }

    [Fact]
    public async Task EveryCustomerWrite_RefusesARequestWithoutTheAntiforgeryToken_BeforeAnythingRuns()
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true);
        var customer = Guid.NewGuid().ToString();

        foreach (var route in RoutesOf(host).Where(r => r.Policy == "Customer" && r.Method != "GET"))
        {
            using var response = await host.Client.SendAsync(Request(route, customer));
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{route.Method} {route.Pattern}");
            (await response.Content.ReadAsStringAsync()).Should().Contain("Csrf.Invalid");
        }

        host.Store.Calls.Should().BeEmpty();
    }

    // ───────────── unexpected failures ─────────────

    [Fact]
    public async Task AnUnexpectedFailure_OutsideDevelopment_AnswersAMinimalProblem_AndNeverLeaksTheException()
    {
        await using var host = await PaymentHttpHost.StartAsync(environment: "Production");
        host.Workflow.Handlers["ListExceptionsAsync"] = _ => throw new InvalidOperationException("secret connection detail Password=hunter2");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments/exceptions");
        request.Headers.Add("X-Test-User", Guid.NewGuid().ToString());
        request.Headers.Add("X-Test-Admin", "1");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Server.Error");
        body.Should().NotContain("hunter2").And.NotContain("secret").And.NotContain("InvalidOperationException").And.NotContain("   at ");
    }

    // ───────────── rate limit ─────────────

    [Fact]
    public async Task FinancialWrites_AreLimitedPerSignedInUser_AdministratorsToo_AndReadsAreNot()
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true, financialWritesPerMinute: 3);
        var admin = Guid.NewGuid();
        var otherAdmin = Guid.NewGuid();
        var route = "/api/admin/payments/exceptions/" + Guid.NewGuid() + "/resolve";

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await host.AdminPostAsync(route, "{}", asUser: admin)).StatusCode);

        statuses.Take(3).Should().OnlyContain(s => s == HttpStatusCode.BadRequest, "within the limit the request is handled (here: rejected as invalid)");
        statuses.Skip(3).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests);
        (await host.AdminPostAsync(route, "{}", asUser: otherAdmin)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "another user has their own allowance");

        using var read = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments/providers");
        read.Headers.Add("X-Test-User", admin.ToString());
        read.Headers.Add("X-Test-Admin", "1");
        for (var i = 0; i < 10; i++)
            (await host.Client.SendAsync(Clone(read))).StatusCode.Should().Be(HttpStatusCode.OK, "reads are not limited");
    }

    [Fact]
    public async Task CustomerPaymentStarts_AreLimitedPerUser()
    {
        await using var host = await PaymentHttpHost.StartAsync(financialWritesPerMinute: 2);
        host.StripeBehaves();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            codes.Add((await host.StartPaymentAsync("{}", key: $"k-{i}")).StatusCode);

        codes.Take(2).Should().NotContain(HttpStatusCode.TooManyRequests);
        codes.Skip(2).Should().OnlyContain(c => c == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Webhooks_AreNotThrottledByTheFinancialWriteLimit_TheirProtectionIsTheSignatureAndTheSizeCap()
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true, financialWritesPerMinute: 1);

        for (var i = 0; i < 12; i++)
        {
            using var response = await host.Client.PostAsync("/api/webhooks/stripe", new StringContent("{}"));
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("5", 5)]
    public void TheFinancialWriteLimit_HasASafeDefault_AndIsConfigurable(string? configured, int expected)
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:FinancialWritesPerMinute"] = configured }).Build();

        PulseReserve.AppHost.Security.SecurityOptions.Resolve(config, new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Development" })
            .FinancialWritesPerMinute.Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void AFinancialWriteLimitBelowOne_FailsStartup(string configured)
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:FinancialWritesPerMinute"] = configured }).Build();

        var act = () => PulseReserve.AppHost.Security.SecurityOptions.Resolve(config, new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Development" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*FinancialWritesPerMinute*");
    }

    private static HttpRequestMessage Clone(HttpRequestMessage source)
    {
        var copy = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return copy;
    }
}
