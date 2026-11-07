using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PulseReserve.IntegrationTests.Catalog;
using PulseReserve.Modules.Reservation.Features.Holds;
using PulseReserve.Modules.Reservation.Features.Inventory;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

// End-to-end seat-hold tests through the real API pipeline against a disposable PostgreSQL container.
// Concurrency tests assert the database invariants directly, not only HTTP outcomes.
public class ReservationApiHttpTests : IClassFixture<PostgreSqlDatabaseFixture>, IDisposable
{
    private readonly PostgreSqlDatabaseFixture _database;
    private readonly CatalogApiFactory _factory;
    private readonly HttpClient _client;

    public ReservationApiHttpTests(PostgreSqlDatabaseFixture database)
    {
        _database = database;
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
    public async Task Availability_SeedsInventoryOnce_AndListsEverySeatAsAvailable()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 4);

        var first = await GetAvailabilityAsync(sale.EventId);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => GetAvailabilityAsync(sale.EventId)));

        first.Seats.Select(s => s.SeatId).Should().BeEquivalentTo(sale.SeatIds);
        first.Seats.Should().OnlyContain(s => s.Status == "Available" && s.PriceAmount == sale.SeatPrice && s.Currency == "USD");
        concurrent.Should().OnlyContain(a => a.Seats.Count == 4);
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}'")).Should().Be(4);
    }

    [Fact]
    public async Task Availability_RejectsEventsThatAreNotOnSale_AndUnknownEvents()
    {
        var published = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 1, startSale: false);

        await AssertProblemAsync(await _client.GetAsync($"/api/events/{published.EventId}/availability"), HttpStatusCode.Conflict, "Reservation.SalesNotOpen");
        await AssertProblemAsync(await _client.GetAsync($"/api/events/{Guid.NewGuid()}/availability"), HttpStatusCode.NotFound, "Event.NotFound");
        await AssertProblemAsync(await HoldAsync(published.EventId, await NewUserAsync(), [published.SeatIds[0]]), HttpStatusCode.Conflict, "Reservation.SalesNotOpen");
    }

    [Fact]
    public async Task Hold_CreatesReservation_AndMarksSeatsHeld()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 3);
        var customer = await NewUserAsync();

        var response = await HoldAsync(sale.EventId, customer, [sale.SeatIds[0], sale.SeatIds[1]]);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var reservation = (await response.Content.ReadFromJsonAsync<ReservationResponse>())!;
        reservation.Status.Should().Be("Held");
        reservation.CustomerId.Should().Be(customer.Id, "ownership comes from the session, never from the request");
        reservation.TotalAmount.Should().Be(2 * sale.SeatPrice);
        reservation.Currency.Should().Be("USD");
        reservation.Seats.Select(s => s.SeatId).Should().BeEquivalentTo([sale.SeatIds[0], sale.SeatIds[1]]);
        reservation.ExpiresAtUtc.Should().BeAfter(reservation.CreatedAtUtc);
        response.Headers.Location!.ToString().Should().Be($"/api/reservations/{reservation.ReservationId}");

        var availability = await GetAvailabilityAsync(sale.EventId);
        availability.Seats.Count(s => s.Status == "Held").Should().Be(2);
        availability.Seats.Single(s => s.SeatId == sale.SeatIds[2]).Status.Should().Be("Available");
    }

    [Fact]
    public async Task ConcurrentHoldsForTheSameSeat_ExactlyOneSucceeds()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 1);
        await GetAvailabilityAsync(sale.EventId); // seed inventory so the race is purely on the seat

        var users = await NewUsersAsync(50);
        var responses = await Task.WhenAll(users.Select(u => HoldAsync(sale.EventId, u, [sale.SeatIds[0]])));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(49);
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            await AssertProblemAsync(conflict, HttpStatusCode.Conflict, "Reservation.SeatsUnavailable");

        (await ScalarAsync($"SELECT count(*) FROM reservation.reservations WHERE event_id = '{sale.EventId}' AND status = 'Held'")).Should().Be(1);
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Held'")).Should().Be(1);
    }

    [Fact]
    public async Task OverlappingMultiSeatHolds_AreAllOrNothing_AndNeverDoubleBook()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 8);
        await GetAvailabilityAsync(sale.EventId);
        var random = new Random(20261020);
        var requests = Enumerable.Range(0, 40)
            .Select(_ => sale.SeatIds.OrderBy(_ => random.Next()).Take(3).ToList())
            .ToList();

        var users = await NewUsersAsync(requests.Count);
        var responses = await Task.WhenAll(requests.Select((seats, i) => HoldAsync(sale.EventId, users[i], seats)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        var winners = new List<ReservationResponse>();
        foreach (var response in responses)
        {
            if (response.StatusCode == HttpStatusCode.Created)
            {
                winners.Add((await response.Content.ReadFromJsonAsync<ReservationResponse>())!);
            }
            else
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                problem.GetProperty("unavailableSeatIds").GetArrayLength().Should().BeGreaterThan(0);
            }
        }

        winners.Should().NotBeEmpty();
        var wonSeats = winners.SelectMany(w => w.Seats.Select(s => s.SeatId)).ToList();
        wonSeats.Should().OnlyHaveUniqueItems("a seat may belong to at most one active reservation");
        winners.Should().OnlyContain(w => w.Seats.Count == 3, "a hold is all-or-nothing");

        // Database invariants: every held seat belongs to exactly one winning reservation; nothing partial was left behind.
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Held'")).Should().Be(wonSeats.Count);
        (await ScalarAsync($"SELECT count(*) FROM reservation.reservations WHERE event_id = '{sale.EventId}'")).Should().Be(winners.Count);
        (await ScalarAsync(
            $"""
             SELECT count(*) FROM reservation.reservation_items i
             JOIN reservation.event_seats s ON s.event_id = i.event_id AND s.seat_id = i.seat_id
             WHERE i.event_id = '{sale.EventId}' AND s.reservation_id IS DISTINCT FROM i.reservation_id
             """)).Should().Be(0);
    }

    [Fact]
    public async Task RetryWithSameIdempotencyKey_ReturnsTheOriginalReservation()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 2);
        var customer = await NewUserAsync();
        const string key = "retry-key-1";

        var first = await HoldAsync(sale.EventId, customer, [sale.SeatIds[0]], key);
        var retry = await HoldAsync(sale.EventId, customer, [sale.SeatIds[0]], key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.Headers.GetValues("Idempotent-Replayed").Should().Equal("true");
        (await retry.Content.ReadFromJsonAsync<ReservationResponse>())!.ReservationId
            .Should().Be((await first.Content.ReadFromJsonAsync<ReservationResponse>())!.ReservationId);

        await AssertProblemAsync(await HoldAsync(sale.EventId, customer, [sale.SeatIds[1]], key), HttpStatusCode.Conflict, "Reservation.IdempotencyKeyReused");
        await AssertProblemAsync(await HoldAsync(sale.EventId, customer, [sale.SeatIds[1]], idempotencyKey: null), HttpStatusCode.BadRequest, "Reservation.MissingIdempotencyKey");
    }

    [Fact]
    public async Task ConcurrentRetriesWithSameKey_CreateExactlyOneReservation()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 1);
        await GetAvailabilityAsync(sale.EventId);
        var customer = await NewUserAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => HoldAsync(sale.EventId, customer, [sale.SeatIds[0]], "concurrent-key")));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<ReservationResponse>())!.ReservationId));
        ids.Distinct().Should().ContainSingle();
        (await ScalarAsync($"SELECT count(*) FROM reservation.reservations WHERE customer_id = '{customer.Id}'")).Should().Be(1);
    }

    [Fact]
    public async Task SecondActiveHoldBySameCustomerForSameEvent_IsRejected()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 2);
        var customer = await NewUserAsync();

        (await HoldAsync(sale.EventId, customer, [sale.SeatIds[0]])).StatusCode.Should().Be(HttpStatusCode.Created);
        await AssertProblemAsync(await HoldAsync(sale.EventId, customer, [sale.SeatIds[1]]), HttpStatusCode.Conflict, "Reservation.ActiveHoldExists");

        (await GetAvailabilityAsync(sale.EventId)).Seats.Single(s => s.SeatId == sale.SeatIds[1]).Status.Should().Be("Available");
    }

    [Fact]
    public async Task Release_FreesSeats_IsIdempotent_AndHidesOtherCustomersReservations()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 2);
        var customer = await NewUserAsync();
        var reservation = await HoldReservationAsync(sale.EventId, customer, [sale.SeatIds[0], sale.SeatIds[1]]);

        var other = await NewUserAsync();
        await AssertProblemAsync(await other.Client.PostAsync($"/api/reservations/{reservation.ReservationId}/release", null), HttpStatusCode.NotFound, "Reservation.NotFound");
        await AssertProblemAsync(await other.Client.GetAsync($"/api/reservations/{reservation.ReservationId}"), HttpStatusCode.NotFound, "Reservation.NotFound");
        (await GetAvailabilityAsync(sale.EventId)).Seats.Count(x => x.Status == "Held").Should().Be(2, "another user's release attempt changed nothing");

        var released = await customer.Client.PostAsync($"/api/reservations/{reservation.ReservationId}/release", null);
        var releasedAgain = await customer.Client.PostAsync($"/api/reservations/{reservation.ReservationId}/release", null);

        released.StatusCode.Should().Be(HttpStatusCode.OK);
        (await released.Content.ReadFromJsonAsync<ReservationResponse>())!.Status.Should().Be("Released");
        releasedAgain.StatusCode.Should().Be(HttpStatusCode.OK);
        (await releasedAgain.Content.ReadFromJsonAsync<ReservationResponse>())!.Status.Should().Be("Released");
        (await GetAvailabilityAsync(sale.EventId)).Seats.Should().OnlyContain(s => s.Status == "Available");

        // The customer may hold again once the previous hold is released.
        (await HoldAsync(sale.EventId, customer, [sale.SeatIds[0]])).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ExpiredHold_IsReclaimable_AndReleasingItLaterDoesNotTouchTheNewHolder()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 1);
        var first = await NewUserAsync();
        var second = await NewUserAsync();
        var expired = await HoldReservationAsync(sale.EventId, first, [sale.SeatIds[0]]);
        await ExpireAsync(expired.ReservationId);

        (await GetAvailabilityAsync(sale.EventId)).Seats.Single().Status.Should().Be("Available");
        var reclaimed = await HoldReservationAsync(sale.EventId, second, [sale.SeatIds[0]]);

        var read = await first.Client.GetFromJsonAsync<ReservationResponse>($"/api/reservations/{expired.ReservationId}");
        read!.Status.Should().Be("Expired");

        var release = await first.Client.PostAsync($"/api/reservations/{expired.ReservationId}/release", null);
        (await release.Content.ReadFromJsonAsync<ReservationResponse>())!.Status.Should().Be("Expired");

        (await GetAvailabilityAsync(sale.EventId)).Seats.Single().Status.Should().Be("Held");
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE reservation_id = '{reclaimed.ReservationId}'")).Should().Be(1);
    }

    [Fact]
    public async Task CustomersOwnExpiredHold_DoesNotBlockANewHold()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 2);
        var customer = await NewUserAsync();
        var old = await HoldReservationAsync(sale.EventId, customer, [sale.SeatIds[0]]);
        await ExpireAsync(old.ReservationId);

        (await HoldAsync(sale.EventId, customer, [sale.SeatIds[1]])).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ScalarAsync($"SELECT count(*) FROM reservation.reservations WHERE id = '{old.ReservationId}' AND status = 'Expired'")).Should().Be(1);
        (await GetAvailabilityAsync(sale.EventId)).Seats.Single(s => s.SeatId == sale.SeatIds[0]).Status.Should().Be("Available");
    }

    [Fact]
    public async Task ActiveHold_IsFoundFromAnotherSession_ForItsOwnerOnly_AndNotOnceItHasEnded()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 3);
        var owner = await NewUserAsync();
        var stranger = await NewUserAsync();
        var held = await HoldReservationAsync(sale.EventId, owner, [sale.SeatIds[0]]);

        // A second browser: the same account signs in again and asks what it holds for this event.
        using var secondBrowser = _factory.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(secondBrowser);
        var login = await secondBrowser.PostAsJsonAsync("/api/identity/login", new { email = owner.Email, password = AuthTestSupport.Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        var found = await secondBrowser.GetAsync($"/api/reservations/active?eventId={sale.EventId}");
        found.StatusCode.Should().Be(HttpStatusCode.OK, await found.Content.ReadAsStringAsync());
        var reservation = (await found.Content.ReadFromJsonAsync<ReservationResponse>())!;
        reservation.ReservationId.Should().Be(held.ReservationId);
        reservation.Status.Should().Be("Held");
        reservation.Seats.Select(s => s.SeatId).Should().BeEquivalentTo([sale.SeatIds[0]]);

        // Nobody else can see it, and a signed-out caller is refused.
        (await stranger.Client.GetAsync($"/api/reservations/active?eventId={sale.EventId}")).StatusCode.Should().Be(HttpStatusCode.NoContent, "\"none\" is a normal answer, not an error");
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync($"/api/reservations/active?eventId={sale.EventId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await owner.Client.GetAsync("/api/reservations/active")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Once the hold has passed its expiry on the database clock it is no longer "active", even before anything closed it.
        await ExpireAsync(held.ReservationId);
        (await owner.Client.GetAsync($"/api/reservations/active?eventId={sale.EventId}")).StatusCode.Should().Be(HttpStatusCode.NoContent, "\"none\" is a normal answer, not an error");

        // A new hold is found again; releasing it ends it.
        var next = await HoldReservationAsync(sale.EventId, owner, [sale.SeatIds[1]]);
        (await owner.Client.GetFromJsonAsync<ReservationResponse>($"/api/reservations/active?eventId={sale.EventId}"))!.ReservationId.Should().Be(next.ReservationId);
        (await owner.Client.PostAsync($"/api/reservations/{next.ReservationId}/release", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.Client.GetAsync($"/api/reservations/active?eventId={sale.EventId}")).StatusCode.Should().Be(HttpStatusCode.NoContent, "\"none\" is a normal answer, not an error");
    }

    [Fact]
    public async Task InvalidHoldRequests_Return400()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 1);

        var user = await NewUserAsync();

        await AssertProblemAsync(await HoldAsync(sale.EventId, user, [Guid.NewGuid()]), HttpStatusCode.BadRequest, "Reservation.UnknownSeats");
        await AssertProblemAsync(await HoldAsync(sale.EventId, user, []), HttpStatusCode.BadRequest, "Reservation.NoSeats");
        await AssertProblemAsync(await HoldAsync(sale.EventId, user, Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToList()), HttpStatusCode.BadRequest, "Reservation.TooManySeats");
        await AssertProblemAsync(await HoldAsync(sale.EventId, user, [sale.SeatIds[0], sale.SeatIds[0]]), HttpStatusCode.BadRequest, "Reservation.InvalidSeatIds");
    }

    [Fact]
    public async Task ClientSuppliedCustomerIds_AreIgnored_OwnershipIsTheSessionUser()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 2);
        var victim = await NewUserAsync();
        var attacker = await NewUserAsync();
        var victimReservation = await HoldReservationAsync(sale.EventId, victim, [sale.SeatIds[0]]);

        // The attacker names the victim as customer in body and query; both are ignored.
        var forged = new HttpRequestMessage(HttpMethod.Post, "/api/reservations")
        {
            Content = JsonContent.Create(new { eventId = sale.EventId, customerId = victim.Id, seatIds = new[] { sale.SeatIds[1] } })
        };
        forged.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var created = await attacker.Client.SendAsync(forged);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await created.Content.ReadFromJsonAsync<ReservationResponse>())!.CustomerId.Should().Be(attacker.Id);

        await AssertProblemAsync(await attacker.Client.GetAsync($"/api/reservations/{victimReservation.ReservationId}?customerId={victim.Id}"), HttpStatusCode.NotFound, "Reservation.NotFound");
        await AssertProblemAsync(await attacker.Client.PostAsync($"/api/reservations/{victimReservation.ReservationId}/release?customerId={victim.Id}", null), HttpStatusCode.NotFound, "Reservation.NotFound");

        var stillHeld = await victim.Client.GetFromJsonAsync<ReservationResponse>($"/api/reservations/{victimReservation.ReservationId}");
        stillHeld!.Status.Should().Be("Held");
        stillHeld.CustomerId.Should().Be(victim.Id);
    }

    [Fact]
    public async Task SameIdempotencyKey_FromDifferentUsers_NeverCollides()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_client, seatCount: 2);
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();

        var a = await HoldAsync(sale.EventId, alice, [sale.SeatIds[0]], "shared-key");
        var b = await HoldAsync(sale.EventId, bob, [sale.SeatIds[1]], "shared-key");

        a.StatusCode.Should().Be(HttpStatusCode.Created);
        b.StatusCode.Should().Be(HttpStatusCode.Created, "keys are scoped to the authenticated user");
    }

    private Task<TestUser> NewUserAsync() => AuthTestSupport.RegisterAsync(_factory);

    private async Task<IReadOnlyList<TestUser>> NewUsersAsync(int count) =>
        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => NewUserAsync()));

    // The body carries no customer: ownership is the signed-in user's server-derived ID.
    private static Task<HttpResponseMessage> HoldAsync(Guid eventId, TestUser user, IReadOnlyList<Guid> seatIds, string? idempotencyKey = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/reservations")
        {
            Content = JsonContent.Create(new { eventId, seatIds })
        };
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey.Length == 0 ? Guid.NewGuid().ToString() : idempotencyKey);
        return user.Client.SendAsync(request);
    }

    private async Task<ReservationResponse> HoldReservationAsync(Guid eventId, TestUser user, IReadOnlyList<Guid> seatIds)
    {
        var response = await HoldAsync(eventId, user, seatIds);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReservationResponse>())!;
    }

    private async Task<EventAvailabilityResponse> GetAvailabilityAsync(Guid eventId)
    {
        var response = await _client.GetAsync($"/api/events/{eventId}/availability");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<EventAvailabilityResponse>())!;
    }

    // Simulates the passage of the hold window in the disposable test database (holds are never renewed).
    private async Task ExpireAsync(Guid reservationId)
    {
        await using var db = _database.CreateReservationDbContext();
        await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE reservation.reservations
             SET created_at_utc = now() - interval '20 minutes', expires_at_utc = now() - interval '10 minutes'
             WHERE id = {reservationId}
             """);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE reservation.event_seats SET hold_expires_at_utc = now() - interval '10 minutes' WHERE reservation_id = {reservationId}");
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var db = _database.CreateReservationDbContext();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expectedStatus, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        JsonDocument.Parse(body).RootElement.GetProperty("title").GetString().Should().Be(expectedCode);
        body.Should().NotContainAny("Exception", "   at ");
    }
}
