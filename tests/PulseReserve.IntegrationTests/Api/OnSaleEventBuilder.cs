using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace PulseReserve.IntegrationTests.Api;

/// <summary>Creates catalog data through the real Catalog HTTP API (venue → layout → event → price → publish → sale).</summary>
internal static class OnSaleEventBuilder
{
    public sealed record SaleSetup(Guid EventId, IReadOnlyList<Guid> SeatIds, decimal SeatPrice, string Currency);

    public static async Task<SaleSetup> CreateAsync(HttpClient client, int seatCount, bool startSale = true, decimal price = 50m)
    {
        var venueId = await CreateAsync(client, "venues", new { name = "Arena", street = "1 Main St", city = "Cairo", country = "EG", postalCode = "11511" });
        var layoutId = await CreateAsync(client, "seating-layouts", new { venueId, name = "Main" });

        await PostOkAsync(client, $"seating-layouts/{layoutId}/price-tiers", new { name = "Standard" });
        await PostOkAsync(client, $"seating-layouts/{layoutId}/sections", new { name = "Floor", displayOrder = 1 });
        var layout = await GetAsync(client, $"seating-layouts/{layoutId}");
        var tierId = WireId(layout.GetProperty("priceTiers")[0].GetProperty("id"));
        var sectionId = WireId(layout.GetProperty("sections")[0].GetProperty("id"));

        await PostOkAsync(client, $"seating-layouts/{layoutId}/rows", new { sectionId, label = "A", displayOrder = 1 });
        var rowId = WireId((await GetAsync(client, $"seating-layouts/{layoutId}")).GetProperty("rows")[0].GetProperty("id"));

        for (var i = 1; i <= seatCount; i++)
            await PostOkAsync(client, $"seating-layouts/{layoutId}/seats", new { rowId, sectionId, label = i.ToString(), coordinateX = i, coordinateY = 0, priceTierId = tierId });

        var eventId = await CreateAsync(client, "events", new
        {
            name = "Concert",
            description = "Reservation test",
            venueId,
            scheduledStartUtc = "2030-01-01T20:00:00Z",
            scheduledEndUtc = "2030-01-01T23:00:00Z",
            seatingLayoutId = layoutId
        });
        await PostOkAsync(client, $"events/{eventId}/prices", new { priceTierId = tierId, priceAmount = price, currency = "USD" });
        await PostOkAsync(client, $"events/{eventId}/publish", new { });
        if (startSale)
            await PostOkAsync(client, $"events/{eventId}/start-sale", new { });

        var seats = (await GetAsync(client, $"seating-layouts/{layoutId}")).GetProperty("seats")
            .EnumerateArray().Select(s => WireId(s.GetProperty("id"))).Order().ToList();
        return new SaleSetup(eventId, seats, price, "USD");
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/catalog/{path}", body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task PostOkAsync(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/catalog/{path}", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync($"/api/catalog/{path}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Guid WireId(JsonElement id) => id.GetProperty("value").GetGuid();
}
