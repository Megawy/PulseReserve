using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PulseReserve.AppHost.Security;
using PulseReserve.IntegrationTests.Catalog;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

// The authorization boundary of the Catalog API, run through the real pipeline against disposable PostgreSQL.
//
// Route classification:
//   public read      GET  venues/{id}, seating-layouts/{id}, events, events/{id}
//   admin mutation   every other route (11 today, listed in WriteRoutes)
//   (customer action: none in Catalog; customers use the Reservation API)
public class CatalogSecurityTests : IClassFixture<PostgreSqlDatabaseFixture>, IDisposable
{
    private static readonly string Id = Guid.NewGuid().ToString();

    public static TheoryData<string> WriteRoutes =>
    [
        "/api/catalog/venues",
        "/api/catalog/seating-layouts",
        $"/api/catalog/seating-layouts/{Guid.NewGuid()}/sections",
        $"/api/catalog/seating-layouts/{Guid.NewGuid()}/rows",
        $"/api/catalog/seating-layouts/{Guid.NewGuid()}/seats",
        $"/api/catalog/seating-layouts/{Guid.NewGuid()}/price-tiers",
        $"/api/catalog/seating-layouts/{Guid.NewGuid()}/lock",
        "/api/catalog/events",
        $"/api/catalog/events/{Guid.NewGuid()}/prices",
        $"/api/catalog/events/{Guid.NewGuid()}/publish",
        $"/api/catalog/events/{Guid.NewGuid()}/start-sale"
    ];

    private readonly PostgreSqlDatabaseFixture _database;
    private readonly CatalogApiFactory _factory;

    public CatalogSecurityTests(PostgreSqlDatabaseFixture database)
    {
        _database = database;
        _factory = new CatalogApiFactory(database);
    }

    public void Dispose() => _factory.Dispose();

    private static bool IsRead(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path) =>
        await client.PostAsJsonAsync(path, new { });

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        JsonDocument.Parse(body).RootElement.GetProperty("title").GetString().Should().Be(title);
    }

    [Fact]
    public void EveryCatalogRoute_IsClassifiedAndProtectedByTheEndpointMetadata()
    {
        _ = _factory.CreateClient();
        var endpoints = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/catalog", StringComparison.OrdinalIgnoreCase))
            .ToList();
        endpoints.Should().NotBeEmpty();

        var writes = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            if (methods.All(IsRead))
            {
                policies.Should().BeEmpty($"{endpoint.DisplayName} is a public read");
                continue;
            }

            policies.Should().Contain(AuthPolicies.Admin, $"{endpoint.DisplayName} mutates the catalog");
            writes.Add(endpoint.RoutePattern.RawText!);
        }

        // Adding a Catalog write route without extending WriteRoutes (and therefore these tests) fails here.
        writes.Should().HaveCount(WriteRoutes.Count);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task Anonymous_CannotCallAnyCatalogWrite_Returns401(string path)
    {
        var client = _factory.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(client);

        await AssertProblemAsync(await PostAsync(client, path), HttpStatusCode.Unauthorized, "Auth.Unauthorized");
    }

    [Fact]
    public async Task Anonymous_WithoutAnyCsrfToken_StillGets401_NotAnInformationLeak()
    {
        var client = _factory.CreateClient();
        await AssertProblemAsync(await PostAsync(client, "/api/catalog/venues"), HttpStatusCode.Unauthorized, "Auth.Unauthorized");
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task AuthenticatedCustomer_CannotCallAnyCatalogWrite_Returns403(string path)
    {
        var customer = await AuthTestSupport.RegisterAsync(_factory);

        await AssertProblemAsync(await PostAsync(customer.Client, path), HttpStatusCode.Forbidden, "Auth.Forbidden");
    }

    [Fact]
    public async Task CustomerWrite_ChangesNothingInTheDatabase()
    {
        var customer = await AuthTestSupport.RegisterAsync(_factory);
        var name = $"Hacked {Guid.NewGuid():N}";

        var response = await customer.Client.PostAsJsonAsync("/api/catalog/venues",
            new { name, street = "1 St", city = "Cairo", country = "EG", postalCode = "11511" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using var db = _database.CreateDbContext();
        (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.Venues, v => v.Name == name)).Should().Be(0);
    }

    [Fact]
    public async Task Admin_CanPerformTheFullCatalogLifecycle()
    {
        var admin = await AdminTestSupport.RegisterAdminAsync(_factory);

        var sale = await OnSaleEventBuilder.CreateAsync(admin.Client, seatCount: 2);

        var details = await admin.Client.GetAsync($"/api/catalog/events/{sale.EventId}");
        details.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await details.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString().Should().Be("OnSale");
    }

    [Fact]
    public async Task Admin_StillNeedsAValidCsrfToken()
    {
        var admin = await AdminTestSupport.RegisterAdminAsync(_factory);
        admin.Client.DefaultRequestHeaders.Remove(AuthTestSupport.CsrfHeader);

        var withoutToken = await admin.Client.PostAsJsonAsync("/api/catalog/venues",
            new { name = "Arena", street = "1 St", city = "Cairo", country = "EG", postalCode = "11511" });
        await AssertProblemAsync(withoutToken, HttpStatusCode.Forbidden, "Csrf.Invalid");

        admin.Client.DefaultRequestHeaders.Add(AuthTestSupport.CsrfHeader, "forged");
        await AssertProblemAsync(await PostAsync(admin.Client, "/api/catalog/venues"), HttpStatusCode.Forbidden, "Csrf.Invalid");
    }

    [Fact]
    public async Task PublicCatalogReads_WorkAnonymously()
    {
        var admin = await AdminTestSupport.RegisterAdminAsync(_factory);
        var sale = await OnSaleEventBuilder.CreateAsync(admin.Client, seatCount: 1);
        var anonymous = _factory.CreateClient();

        var list = await anonymous.GetAsync("/api/catalog/events");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync()).Should().Contain(sale.EventId.ToString());
        (await anonymous.GetAsync($"/api/catalog/events/{sale.EventId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync($"/api/events/{sale.EventId}/availability")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync($"/api/catalog/venues/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync($"/api/catalog/seating-layouts/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Registration_CannotCreateAnAdmin_EvenIfTheRequestAsksForIt()
    {
        var client = _factory.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(client);

        var response = await client.PostAsJsonAsync("/api/identity/register", new
        {
            email = $"sneaky-{Guid.NewGuid():N}@example.com",
            password = AuthTestSupport.Password,
            displayName = "Sneaky",
            isAdmin = true,
            role = "Admin",
            roles = new[] { "Admin" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        session.GetProperty("isAdmin").GetBoolean().Should().BeFalse();
        AuthTestSupport.ApplyToken(client, session);
        await AssertProblemAsync(await PostAsync(client, "/api/catalog/venues"), HttpStatusCode.Forbidden, "Auth.Forbidden");
    }

    [Fact]
    public void NoPublicEndpointCanGrantAdmin()
    {
        _ = _factory.CreateClient();
        var routes = _factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!).Where(r => r.StartsWith("/api/identity", StringComparison.OrdinalIgnoreCase)).ToList();

        routes.Should().BeEquivalentTo(["/api/identity/session", "/api/identity/register", "/api/identity/login", "/api/identity/logout"]);
    }

    [Fact]
    public async Task GrantingAndRevokingAdmin_TakesEffectOnAnExistingSession()
    {
        var user = await AuthTestSupport.RegisterAsync(_factory);
        var venue = new { name = "Arena", street = "1 St", city = "Cairo", country = "EG", postalCode = "11511" };

        (await user.Client.PostAsJsonAsync("/api/catalog/venues", venue)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.Client.GetFromJsonAsync<JsonElement>("/api/identity/session")).GetProperty("isAdmin").GetBoolean().Should().BeFalse();

        AdminTestSupport.Grant(_factory, user.Id); // no re-login: the same cookie
        (await user.Client.PostAsJsonAsync("/api/catalog/venues", venue)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await user.Client.GetFromJsonAsync<JsonElement>("/api/identity/session")).GetProperty("isAdmin").GetBoolean().Should().BeTrue();

        AdminTestSupport.Revoke(_factory, user.Id);
        (await user.Client.PostAsJsonAsync("/api/catalog/venues", venue)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.Client.GetFromJsonAsync<JsonElement>("/api/identity/session")).GetProperty("isAdmin").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AdminListedByAnotherUsersId_DoesNotMakeAnyoneElseAdmin()
    {
        var admin = await AdminTestSupport.RegisterAdminAsync(_factory);
        var other = await AuthTestSupport.RegisterAsync(_factory);

        (await PostAsync(other.Client, "/api/catalog/venues")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync(admin.Client, "/api/catalog/venues")).StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }
}

public class AdminConfigurationTests
{
    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301,oops")]
    public void InvalidAdminList_FailsStartup(string value)
    {
        var act = () => AdminRegistry.ParseOrThrow(value);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Identity:AdminUserIds*");
    }

    [Fact]
    public void ValidList_ParsesCommaAndSemicolonSeparatedIds_AndEmptyMeansNobody()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        AdminRegistry.ParseOrThrow($" {a} ; {b} ,").Should().BeEquivalentTo([a, b]);
        AdminRegistry.ParseOrThrow(null).Should().BeEmpty();
        AdminRegistry.ParseOrThrow("  ").Should().BeEmpty();
    }

    [Fact]
    public void AdminRegistry_NeverGrantsOnADamagedRuntimeValue_OrEmptyGuid()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AdminRegistry.ConfigKey] = "garbage,00000000-0000-0000-0000-000000000000"
        }).Build();
        var registry = new AdminRegistry(config);

        registry.IsAdmin(Guid.Empty).Should().BeFalse();
        registry.IsAdmin(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void SecurityOptions_Resolve_RejectsAMisconfiguredAdminList()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AdminRegistry.ConfigKey] = "admin@example.com" // emails are deliberately not accepted
        }).Build();

        var act = () => SecurityOptions.Resolve(config, new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Development" });
        act.Should().Throw<InvalidOperationException>();
    }
}
