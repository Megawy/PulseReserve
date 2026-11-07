using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PulseReserve.Infrastructure.Persistence.Catalog;
using PulseReserve.IntegrationTests.Catalog;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

// HTTP-level verification of the Catalog API status mapping, run through the real AppHost pipeline
// against a disposable PostgreSQL container. Lifecycle errors without an existing route are covered in ErrorMappingTests.
public class CatalogApiHttpTests : IClassFixture<PostgreSqlDatabaseFixture>, IDisposable
{
    private readonly CatalogApiFactory _factory;
    private readonly HttpClient _client;

    public CatalogApiHttpTests(PostgreSqlDatabaseFixture database)
    {
        // CreateClient() builds the host through CatalogApiFactory.CreateHost, which throws before any client exists
        // if the host resolved anything other than this fixture's disposable database.
        _factory = new CatalogApiFactory(database);
        // Catalog writes are Admin-only, so the shared client is an administrator (anonymous access is covered by CatalogSecurityTests).
        _client = AdminTestSupport.RegisterAdmin(_factory).Client;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public void Host_UsesOnlyTheDisposableTestDatabase()
    {
        // Defence in depth: the factory already enforced this before creating the client; re-assert it explicitly.
        using var scope = _factory.Services.CreateScope();
        var resolved = new NpgsqlConnectionStringBuilder(
            scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.GetConnectionString());
        var expected = new NpgsqlConnectionStringBuilder(_factory.ExpectedConnectionString);

        resolved.Host.Should().Be(expected.Host);
        resolved.Port.Should().Be(expected.Port);
        resolved.Database.Should().Be(PostgreSqlDatabaseFixture.DefaultDatabaseName);
    }

    [Fact]
    public async Task EventLifecycle_ReturnsConflictForIllegalTransitions()
    {
        var layout = await CreateSellableLayoutAsync();
        var eventId = await CreateEventAsync(layout.VenueId, layout.LayoutId);

        // Draft → StartSale is illegal.
        await AssertProblemAsync(await PostAsync($"events/{eventId}/start-sale", new { }), HttpStatusCode.Conflict, "Event.NotPublished");

        (await PostAsync($"events/{eventId}/prices", new { priceTierId = layout.PriceTierId, priceAmount = 50m, currency = "USD" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync($"events/{eventId}/publish", new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Intentional behaviour change: republishing a Published event used to succeed; it is now a 409 conflict.
        await AssertProblemAsync(await PostAsync($"events/{eventId}/publish", new { }), HttpStatusCode.Conflict, "Event.AlreadyPublished");

        (await PostAsync($"events/{eventId}/start-sale", new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        // OnSale events can neither be republished nor repriced, and their layout is locked.
        await AssertProblemAsync(await PostAsync($"events/{eventId}/publish", new { }), HttpStatusCode.Conflict, "Event.AlreadyPublished");
        await AssertProblemAsync(await PostAsync($"events/{eventId}/start-sale", new { }), HttpStatusCode.Conflict, "Event.NotPublished");
        await AssertProblemAsync(
            await PostAsync($"events/{eventId}/prices", new { priceTierId = layout.PriceTierId, priceAmount = 60m, currency = "USD" }),
            HttpStatusCode.Conflict, "Event.CannotModifyOnSalePricing");
        await AssertProblemAsync(
            await PostAsync($"seating-layouts/{layout.LayoutId}/sections", new { name = "Balcony", displayOrder = 2 }),
            HttpStatusCode.Conflict, "SeatingLayout.Locked");

        var stored = await GetJsonAsync($"events/{eventId}");
        stored.GetProperty("status").GetString().Should().Be("OnSale");
    }

    [Fact]
    public async Task ListEvents_ReturnsOnlyCustomerVisibleEvents()
    {
        var published = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 1, startSale: false);
        var onSale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 1);
        var draft = await CreateDraftEventAsync();

        var response = await _client.GetAsync("/api/catalog/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray()
            .Select(e => e.GetProperty("id").GetProperty("value").GetGuid())
            .ToList();
        ids.Should().Contain([published.EventId, onSale.EventId]);
        ids.Should().NotContain(draft);
    }

    private async Task<Guid> CreateDraftEventAsync()
    {
        var venue = await _client.PostAsJsonAsync("/api/catalog/venues", new { name = "Hall", street = "2 Side St", city = "Cairo", country = "EG", postalCode = "11511" });
        var venueId = (await venue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var layout = await _client.PostAsJsonAsync("/api/catalog/seating-layouts", new { venueId, name = "Draft layout" });
        var layoutId = (await layout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await _client.PostAsJsonAsync("/api/catalog/events", new
        {
            name = "Draft only",
            description = "Never public",
            venueId,
            scheduledStartUtc = "2030-02-01T20:00:00Z",
            scheduledEndUtc = "2030-02-01T23:00:00Z",
            seatingLayoutId = layoutId
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task MissingResources_Return404()
    {
        await AssertProblemAsync(await _client.GetAsync($"/api/catalog/events/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "Event.NotFound");
        await AssertProblemAsync(await _client.GetAsync($"/api/catalog/venues/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "Venue.NotFound");
        await AssertProblemAsync(
            await PostAsync($"events/{Guid.NewGuid()}/publish", new { }), HttpStatusCode.NotFound, "Event.NotFound");
    }

    [Fact]
    public async Task InvalidInput_Returns400()
    {
        await AssertProblemAsync(
            await PostAsync("venues", new { name = "", street = "1 Main St", city = "Cairo", country = "EG", postalCode = "" }),
            HttpStatusCode.BadRequest, "Venue.EmptyName");

        // A reference to a non-existent section inside the request body is invalid input (previously mapped to 404).
        var venueId = await CreateAsync("venues", new { name = "Hall", street = "1 Main St", city = "Cairo", country = "EG", postalCode = "" });
        var layoutId = await CreateAsync("seating-layouts", new { venueId, name = "Main" });
        await AssertProblemAsync(
            await PostAsync($"seating-layouts/{layoutId}/rows", new { sectionId = Guid.NewGuid(), label = "A", displayOrder = 1 }),
            HttpStatusCode.BadRequest, "SeatingLayout.ParentSectionNotFound");
    }

    private async Task<(Guid VenueId, Guid LayoutId, Guid PriceTierId)> CreateSellableLayoutAsync()
    {
        var venueId = await CreateAsync("venues", new { name = "Arena", street = "1 Main St", city = "Cairo", country = "EG", postalCode = "11511" });
        var layoutId = await CreateAsync("seating-layouts", new { venueId, name = "End Stage" });

        (await PostAsync($"seating-layouts/{layoutId}/price-tiers", new { name = "VIP" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync($"seating-layouts/{layoutId}/sections", new { name = "Floor", displayOrder = 1 })).StatusCode.Should().Be(HttpStatusCode.OK);

        var afterSection = await GetJsonAsync($"seating-layouts/{layoutId}");
        var priceTierId = WireId(afterSection.GetProperty("priceTiers")[0].GetProperty("id"));
        var sectionId = WireId(afterSection.GetProperty("sections")[0].GetProperty("id"));

        (await PostAsync($"seating-layouts/{layoutId}/rows", new { sectionId, label = "A", displayOrder = 1 })).StatusCode.Should().Be(HttpStatusCode.OK);
        var rowId = WireId((await GetJsonAsync($"seating-layouts/{layoutId}")).GetProperty("rows")[0].GetProperty("id"));

        (await PostAsync($"seating-layouts/{layoutId}/seats",
            new { rowId, sectionId, label = "1", coordinateX = 0, coordinateY = 0, priceTierId })).StatusCode.Should().Be(HttpStatusCode.OK);

        return (venueId, layoutId, priceTierId);
    }

    private Task<Guid> CreateEventAsync(Guid venueId, Guid layoutId) =>
        CreateAsync("events", new
        {
            name = "Concert",
            description = "HTTP lifecycle test",
            venueId,
            scheduledStartUtc = "2030-01-01T20:00:00Z",
            scheduledEndUtc = "2030-01-01T23:00:00Z",
            seatingLayoutId = layoutId
        });

    private async Task<Guid> CreateAsync(string relativePath, object body)
    {
        var response = await PostAsync(relativePath, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> PostAsync(string relativePath, object body) =>
        _client.PostAsJsonAsync($"/api/catalog/{relativePath}", body);

    private async Task<JsonElement> GetJsonAsync(string relativePath)
    {
        var response = await _client.GetAsync($"/api/catalog/{relativePath}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Strongly typed IDs are serialized as objects: { "value": "<guid>" }.
    private static Guid WireId(JsonElement id) => id.GetProperty("value").GetGuid();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expectedStatus, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("title").GetString().Should().Be(expectedCode);
        problem.GetProperty("status").GetInt32().Should().Be((int)expectedStatus);
        body.Should().NotContainAny("Exception", "   at ");
    }
}
