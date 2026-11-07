using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PulseReserve.AppHost.Api;
using PulseReserve.IntegrationTests.Catalog;
using PulseReserve.Modules.Reservation.Features.Holds;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

// Checkout, orders and the payment simulator through the real pipeline against disposable PostgreSQL.
// Time-dependent cases use either a real database-clock expiry (a factory with a 3 second hold) or a deterministic
// row-lock handshake, never sleeps that merely hope for an interleaving.
public class CheckoutApiHttpTests : IClassFixture<PostgreSqlDatabaseFixture>, IDisposable
{
    private readonly PostgreSqlDatabaseFixture _database;
    private readonly CatalogApiFactory _factory;
    private readonly HttpClient _admin;
    private readonly List<IDisposable> _disposables = [];

    public CheckoutApiHttpTests(PostgreSqlDatabaseFixture database)
    {
        _database = database;
        _factory = new CatalogApiFactory(database);
        _admin = AdminTestSupport.RegisterAdmin(_factory).Client;
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        foreach (var d in _disposables)
            d.Dispose();
    }

    // ───────────── checkout ─────────────

    [Fact]
    public async Task Checkout_FromAValidHold_CreatesAnOrder_WithServerCalculatedTotalAndSnapshot()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 3, price: 45.5m);
        var user = await NewUserAsync();
        var reservation = await HoldAsync(user, sale, 0, 1);

        // The body names a bogus total and price: both are ignored because only the reservation ID is read.
        var response = await user.Client.SendAsync(Post("/api/orders", new { reservationId = reservation.ReservationId, totalAmount = 0.01m, unitPrice = 0.01m }, Guid.NewGuid().ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
        order.Status.Should().Be("PendingPayment");
        order.Currency.Should().Be("USD");
        order.TotalAmount.Should().Be(91.0m);
        order.Lines.Should().HaveCount(2).And.OnlyContain(l => l.Quantity == 1 && l.UnitPrice == 45.5m && l.LineTotal == 45.5m);
        order.Lines.Select(l => l.SeatId).Should().BeEquivalentTo([sale.SeatIds[0], sale.SeatIds[1]]);
        order.PaymentDeadlineUtc.Should().Be(reservation.ExpiresAtUtc, "the deadline is the original hold expiry; the hold is never extended");
        order.Payment.Should().BeNull();
        response.Headers.Location!.ToString().Should().Be($"/api/orders/{order.OrderId}");

        (await ScalarAsync($"SELECT count(*) FROM payments.orders WHERE reservation_id = '{reservation.ReservationId}'")).Should().Be(1);
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{reservation.ReservationId}'")).Should().Be("Checkout");
    }

    [Fact]
    public async Task OrderSnapshot_IsImmutable_InTheDatabase_AndIndependentOfLaterPriceChanges()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 2, price: 50m);
        var user = await NewUserAsync();
        var order = await CheckoutOkAsync(user, await HoldAsync(user, sale, 0, 1));

        // Even a direct write to the seat price copy cannot change what the order says.
        await ExecAsync($"UPDATE reservation.event_seats SET price_amount = 999 WHERE event_id = '{sale.EventId}'");
        var read = (await (await user.Client.GetAsync($"/api/orders/{order.OrderId}")).Content.ReadFromJsonAsync<OrderResponse>())!;
        read.TotalAmount.Should().Be(100m);
        read.Lines.Should().OnlyContain(l => l.UnitPrice == 50m);

        // The tables themselves refuse snapshot changes and deletions (triggers).
        var updateLine = () => ExecAsync($"UPDATE payments.order_items SET unit_price_amount = 1, line_total_amount = 1 WHERE order_id = '{order.OrderId}'");
        await updateLine.Should().ThrowAsync<PostgresException>().WithMessage("*immutable*");
        var deleteLine = () => ExecAsync($"DELETE FROM payments.order_items WHERE order_id = '{order.OrderId}'");
        await deleteLine.Should().ThrowAsync<PostgresException>();
        var updateTotal = () => ExecAsync($"UPDATE payments.orders SET total_amount = 1 WHERE id = '{order.OrderId}'");
        await updateTotal.Should().ThrowAsync<PostgresException>().WithMessage("*immutable*");
        var deleteOrder = () => ExecAsync($"DELETE FROM payments.orders WHERE id = '{order.OrderId}'");
        await deleteOrder.Should().ThrowAsync<PostgresException>();
        (await ScalarAsync($"SELECT total_amount FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be(100);
    }

    [Fact]
    public async Task Checkout_AfterRelease_IsRejected_AndCreatesNothing()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var user = await NewUserAsync();
        var reservation = await HoldAsync(user, sale, 0);
        (await user.Client.PostAsync($"/api/reservations/{reservation.ReservationId}/release", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertProblemAsync(await CheckoutAsync(user, reservation.ReservationId), HttpStatusCode.Conflict, "Order.ReservationNotActive");
        (await ScalarAsync($"SELECT count(*) FROM payments.orders WHERE reservation_id = '{reservation.ReservationId}'")).Should().Be(0);
    }

    [Fact]
    public async Task Checkout_AfterExpiry_IsRejected_AndCreatesNothing()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var user = await NewUserAsync();
        var reservation = await HoldAsync(user, sale, 0);
        await ExpireReservationAsync(reservation.ReservationId);

        await AssertProblemAsync(await CheckoutAsync(user, reservation.ReservationId), HttpStatusCode.Conflict, "Order.ReservationNotActive");
        (await ScalarAsync($"SELECT count(*) FROM payments.orders WHERE reservation_id = '{reservation.ReservationId}'")).Should().Be(0);
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{reservation.ReservationId}'")).Should().Be("Expired");
    }

    [Fact]
    public async Task Checkout_ThatLosesTheRaceAgainstExpiry_NeverCreatesAPayableOrder()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var user = await NewUserAsync();
        var reservation = await HoldAsync(user, sale, 0);

        // Deterministic race: another transaction owns the reservation row; checkout queues behind it; while it is
        // queued the hold expires; once the row is released checkout must observe the expiry (database clock).
        await using var blocker = _database.CreateReservationDbContext();
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlAsync($"SELECT 1 FROM reservation.reservations WHERE id = {reservation.ReservationId} FOR UPDATE");

        var checkout = CheckoutAsync(user, reservation.ReservationId);
        await Task.Delay(400);
        checkout.IsCompleted.Should().BeFalse("checkout waits for the row lock instead of deciding on stale data");

        await blocker.Database.ExecuteSqlAsync($"UPDATE reservation.reservations SET created_at_utc = now() - interval '20 minutes', expires_at_utc = now() - interval '1 second' WHERE id = {reservation.ReservationId}");
        await tx.CommitAsync();

        await AssertProblemAsync(await checkout, HttpStatusCode.Conflict, "Order.ReservationNotActive");
        (await ScalarAsync($"SELECT count(*) FROM payments.orders WHERE reservation_id = '{reservation.ReservationId}'")).Should().Be(0);
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Available'")).Should().Be(1);
    }

    [Fact]
    public async Task Checkout_ByAnotherCustomer_IsNotFound_AndTheHoldIsUntouched()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var owner = await NewUserAsync();
        var intruder = await NewUserAsync();
        var reservation = await HoldAsync(owner, sale, 0);

        await AssertProblemAsync(await CheckoutAsync(intruder, reservation.ReservationId), HttpStatusCode.NotFound, "Order.ReservationNotFound");

        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{reservation.ReservationId}'")).Should().Be("Held");
        (await ScalarAsync($"SELECT count(*) FROM payments.orders")).Should().BeGreaterThanOrEqualTo(0);
        (await ScalarAsync($"SELECT count(*) FROM payments.orders WHERE reservation_id = '{reservation.ReservationId}'")).Should().Be(0);
    }

    [Fact]
    public async Task Orders_RequireASession_AndACsrfToken()
    {
        var anonymous = _factory.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(anonymous);
        var id = Guid.NewGuid();

        await AssertProblemAsync(await anonymous.SendAsync(Post("/api/orders", new { reservationId = id }, "k")), HttpStatusCode.Unauthorized, "Auth.Unauthorized");
        (await anonymous.GetAsync($"/api/orders/{id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/orders/by-reservation/{id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync($"/api/orders/{id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.SendAsync(Post($"/api/orders/{id}/payments", null, "k"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.SendAsync(Post("/api/dev/payment-simulator/complete", new { orderId = id, attemptId = id, simulatorToken = "x", outcome = "succeed" }, null)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var user = await NewUserAsync();
        user.Client.DefaultRequestHeaders.Remove(AuthTestSupport.CsrfHeader);
        await AssertProblemAsync(await user.Client.SendAsync(Post("/api/orders", new { reservationId = id }, "k")), HttpStatusCode.Forbidden, "Csrf.Invalid");
        await AssertProblemAsync(await user.Client.SendAsync(Post($"/api/orders/{id}/payments", null, "k")), HttpStatusCode.Forbidden, "Csrf.Invalid");
        await AssertProblemAsync(await user.Client.PostAsync($"/api/orders/{id}/cancel", null), HttpStatusCode.Forbidden, "Csrf.Invalid");
        await AssertProblemAsync(await user.Client.SendAsync(Post("/api/dev/payment-simulator/complete", new { orderId = id, attemptId = id, simulatorToken = "x", outcome = "succeed" }, null)), HttpStatusCode.Forbidden, "Csrf.Invalid");
    }

    [Fact]
    public async Task OrderReads_AreOwnerOnly_ForeignOrdersAreNotFound()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        var reservation = await HoldAsync(owner, sale, 0);
        var order = await CheckoutOkAsync(owner, reservation);

        await AssertProblemAsync(await other.Client.GetAsync($"/api/orders/{order.OrderId}"), HttpStatusCode.NotFound, "Order.NotFound");
        await AssertProblemAsync(await other.Client.GetAsync($"/api/orders/by-reservation/{reservation.ReservationId}"), HttpStatusCode.NotFound, "Order.NotFound");
        await AssertProblemAsync(await other.Client.PostAsync($"/api/orders/{order.OrderId}/cancel", null), HttpStatusCode.NotFound, "Order.NotFound");
        await AssertProblemAsync(await other.Client.SendAsync(Post($"/api/orders/{order.OrderId}/payments", null, Guid.NewGuid().ToString())), HttpStatusCode.NotFound, "Order.NotFound");

        (await owner.Client.GetAsync($"/api/orders/{order.OrderId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.Client.GetAsync($"/api/orders/by-reservation/{reservation.ReservationId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OrderHistory_ListsOnlyTheCallersOrders_NewestFirst_AndNeedsASession()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 3);
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        var first = await CheckoutOkAsync(owner, await HoldAsync(owner, sale, 0));
        (await owner.Client.PostAsync($"/api/orders/{first.OrderId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await CheckoutOkAsync(owner, await HoldAsync(owner, sale, 1));
        await CheckoutOkAsync(other, await HoldAsync(other, sale, 2));

        var mine = (await owner.Client.GetFromJsonAsync<List<OrderResponse>>("/api/orders"))!;

        mine.Select(o => o.OrderId).Should().Equal(second.OrderId, first.OrderId);
        mine.Should().OnlyContain(o => o.Lines.Count == 1);
        (await other.Client.GetFromJsonAsync<List<OrderResponse>>("/api/orders"))!.Should().HaveCount(1).And.NotContain(o => o.OrderId == first.OrderId);
        (await _factory.CreateClient().GetAsync("/api/orders")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ───────────── checkout idempotency ─────────────

    [Fact]
    public async Task Checkout_SameKeyAndPayload_ReturnsTheOriginalOrder()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var user = await NewUserAsync();
        var reservation = await HoldAsync(user, sale, 0);

        var first = await CheckoutAsync(user, reservation.ReservationId, "same-key");
        var retry = await CheckoutAsync(user, reservation.ReservationId, "same-key");
        var otherKey = await CheckoutAsync(user, reservation.ReservationId, "another-key");

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.Headers.GetValues("Idempotent-Replayed").Should().Equal("true");
        otherKey.StatusCode.Should().Be(HttpStatusCode.OK, "one reservation can only ever have one order");
        var ids = new[] { first, retry, otherKey }.Select(r => r.Content.ReadFromJsonAsync<OrderResponse>().Result!.OrderId).Distinct();
        ids.Should().ContainSingle();
        (await ScalarAsync($"SELECT count(*) FROM payments.orders WHERE reservation_id = '{reservation.ReservationId}'")).Should().Be(1);
    }

    [Fact]
    public async Task Checkout_SameKeyWithADifferentReservation_IsAConflict_AndMissingKeyIsRejected()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 2);
        var user = await NewUserAsync();
        var first = await HoldAsync(user, sale, 0);
        (await CheckoutAsync(user, first.ReservationId, "reused")).StatusCode.Should().Be(HttpStatusCode.Created);

        await AssertProblemAsync(await CheckoutAsync(user, Guid.NewGuid(), "reused"), HttpStatusCode.Conflict, "Order.IdempotencyKeyReused");
        await AssertProblemAsync(await user.Client.SendAsync(Post("/api/orders", new { reservationId = first.ReservationId }, null)), HttpStatusCode.BadRequest, "Order.MissingIdempotencyKey");
        await AssertProblemAsync(await CheckoutAsync(user, first.ReservationId, new string('k', 101)), HttpStatusCode.BadRequest, "Order.IdempotencyKeyTooLong");
    }

    [Fact]
    public async Task ConcurrentCheckouts_OfTheSameReservation_CreateExactlyOneOrder()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 2);
        var user = await NewUserAsync();
        var reservation = await HoldAsync(user, sale, 0, 1);

        // Half share one key, half use distinct keys: every request must resolve to the same single order.
        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => CheckoutAsync(user, reservation.ReservationId, i % 2 == 0 ? "shared-key" : $"key-{i}")));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK,
            string.Join(" | ", responses.Select(r => r.StatusCode)));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<OrderResponse>())!.OrderId));
        ids.Distinct().Should().ContainSingle();
        (await ScalarAsync($"SELECT count(*) FROM payments.orders WHERE reservation_id = '{reservation.ReservationId}'")).Should().Be(1);
        (await ScalarAsync($"SELECT count(*) FROM payments.order_items WHERE order_id = '{ids[0]}'")).Should().Be(2);
    }

    [Fact]
    public async Task InterruptedCheckout_ReservationInCheckoutWithoutAnOrder_IsResumedByRepeatingTheRequest()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var user = await NewUserAsync();
        var reservation = await HoldAsync(user, sale, 0);
        // State left behind by a crash between "reservation moved to Checkout" and "order inserted".
        await ExecAsync($"UPDATE reservation.reservations SET status = 'Checkout' WHERE id = '{reservation.ReservationId}'");

        var response = await CheckoutAsync(user, reservation.ReservationId, "resume-key");

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        (await ScalarAsync($"SELECT count(*) FROM payments.orders WHERE reservation_id = '{reservation.ReservationId}'")).Should().Be(1);
    }

    // ───────────── interaction with the hold lifecycle ─────────────

    [Fact]
    public async Task ReservationInCheckout_CannotBeReleasedDirectly_AndKeepsItsSeats()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 2);
        var user = await NewUserAsync();
        var order = await CheckoutOkAsync(user, await HoldAsync(user, sale, 0));

        var release = await user.Client.PostAsync($"/api/reservations/{order.ReservationId}/release", null);
        (await release.Content.ReadFromJsonAsync<ReservationResponse>())!.Status.Should().Be("Checkout");
        (await AvailabilityAsync(sale.EventId)).Single(s => s.GetProperty("seatId").GetGuid() == sale.SeatIds[0]).GetProperty("status").GetString().Should().Be("Held");

        // Still one active reservation per customer and event while paying.
        await AssertProblemAsync(await HoldRawAsync(user, sale, 1), HttpStatusCode.Conflict, "Reservation.ActiveHoldExists");
    }

    [Fact]
    public async Task CancelOrder_ReleasesTheSeats_IsIdempotent_AndAllowsANewHold()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 2);
        var user = await NewUserAsync();
        var order = await CheckoutOkAsync(user, await HoldAsync(user, sale, 0));

        var cancelled = await user.Client.PostAsync($"/api/orders/{order.OrderId}/cancel", null);
        var again = await user.Client.PostAsync($"/api/orders/{order.OrderId}/cancel", null);

        cancelled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cancelled.Content.ReadFromJsonAsync<OrderResponse>())!.Status.Should().Be("Cancelled");
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{order.ReservationId}'")).Should().Be("Released");
        (await AvailabilityAsync(sale.EventId)).Should().OnlyContain(s => s.GetProperty("status").GetString() == "Available");
        (await HoldRawAsync(user, sale, 0)).StatusCode.Should().Be(HttpStatusCode.Created);
        await AssertProblemAsync(await PayAsync(user, order.OrderId), HttpStatusCode.Conflict, "Order.NotPayable");
    }

    // ───────────── payments ─────────────

    [Fact]
    public async Task SuccessfulSimulatedPayment_PaysTheOrder_AndSellsTheSeatsPermanently()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 3);
        var user = await NewUserAsync();
        var order = await CheckoutOkAsync(user, await HoldAsync(user, sale, 0, 1));

        var started = await PayOkAsync(user, order.OrderId);
        started.Payment!.Status.Should().Be("Initiated");
        started.Payment.IsSimulated.Should().BeTrue();
        started.Payment.Provider.Should().Be("Simulator");
        started.Payment.SimulatorToken.Should().NotBeNullOrEmpty();

        var paid = await CompleteOkAsync(user, started, succeed: true);

        paid.Status.Should().Be("Paid");
        paid.Payment!.Status.Should().Be("Succeeded");
        paid.Payment.SimulatorToken.Should().BeNull("a finished attempt exposes no token");
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{order.ReservationId}'")).Should().Be("Confirmed");
        var seats = await AvailabilityAsync(sale.EventId);
        seats.Count(s => s.GetProperty("status").GetString() == "Sold").Should().Be(2);
        seats.Single(s => s.GetProperty("seatId").GetGuid() == sale.SeatIds[2]).GetProperty("status").GetString().Should().Be("Available");

        // Sold seats are gone for good; the customer is free to hold other seats again.
        var thief = await NewUserAsync();
        await AssertProblemAsync(await HoldRawAsync(thief, sale, 0), HttpStatusCode.Conflict, "Reservation.SeatsUnavailable");
        (await HoldRawAsync(user, sale, 2)).StatusCode.Should().Be(HttpStatusCode.Created);
        await AssertProblemAsync(await user.Client.PostAsync($"/api/orders/{order.OrderId}/cancel", null), HttpStatusCode.Conflict, "Order.NotCancellable");
        var release = await user.Client.PostAsync($"/api/reservations/{order.ReservationId}/release", null);
        (await release.Content.ReadFromJsonAsync<ReservationResponse>())!.Status.Should().Be("Confirmed");
    }

    [Fact]
    public async Task FailedSimulatedPayment_DoesNotConfirmTheOrder_AndAllowsARetry()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 2);
        var user = await NewUserAsync();
        var order = await CheckoutOkAsync(user, await HoldAsync(user, sale, 0));

        var first = await PayOkAsync(user, order.OrderId);
        var failed = await CompleteOkAsync(user, first, succeed: false);

        failed.Status.Should().Be("PendingPayment", "a failed attempt never confirms or cancels the order");
        failed.Payment!.Status.Should().Be("Failed");
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{order.ReservationId}'")).Should().Be("Checkout");
        (await AvailabilityAsync(sale.EventId)).Single(s => s.GetProperty("seatId").GetGuid() == sale.SeatIds[0]).GetProperty("status").GetString().Should().Be("Held");

        var second = await PayOkAsync(user, order.OrderId);
        second.Payment!.AttemptNumber.Should().Be(2);
        (await CompleteOkAsync(user, second, succeed: true)).Status.Should().Be("Paid");
        (await ScalarAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be(2);
    }

    [Fact]
    public async Task DuplicateCompletion_IsSafe_AndConflictingVerdictsAreRejected()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var user = await NewUserAsync();
        var started = await PayOkAsync(user, (await CheckoutOkAsync(user, await HoldAsync(user, sale, 0))).OrderId);

        var once = await CompleteAsync(user, started, "succeed");
        var twice = await CompleteAsync(user, started, "succeed");

        once.StatusCode.Should().Be(HttpStatusCode.OK);
        twice.StatusCode.Should().Be(HttpStatusCode.OK);
        (await twice.Content.ReadFromJsonAsync<OrderResponse>())!.Status.Should().Be("Paid");
        await AssertProblemAsync(await CompleteAsync(user, started, "fail"), HttpStatusCode.Conflict, "Payment.AlreadyCompleted");
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Sold'")).Should().Be(1);
        (await ScalarAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{started.OrderId}'")).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentDuplicateCompletions_SellOnce()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 2);
        var user = await NewUserAsync();
        var started = await PayOkAsync(user, (await CheckoutOkAsync(user, await HoldAsync(user, sale, 0, 1))).OrderId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => CompleteAsync(user, started, "succeed")));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Sold'")).Should().Be(2);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("Paid");
    }

    [Fact]
    public async Task Completion_MustMatchTheOrderTheAttemptAndTheSecretToken()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 2);
        var victim = await NewUserAsync();
        var attacker = await NewUserAsync();
        var order = await CheckoutOkAsync(victim, await HoldAsync(victim, sale, 0));
        var started = await PayOkAsync(victim, order.OrderId);
        var otherSale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var otherOrder = await CheckoutOkAsync(victim, await HoldAsync(victim, otherSale, 0));

        // wrong token, wrong attempt, attempt of a different order, unknown ids: all look like "not found".
        await AssertProblemAsync(await CompleteRawAsync(victim, started.OrderId, started.Payment!.AttemptId, "sim_wrong", "succeed"), HttpStatusCode.NotFound, "Payment.AttemptNotFound");
        await AssertProblemAsync(await CompleteRawAsync(victim, started.OrderId, Guid.NewGuid(), started.Payment.SimulatorToken, "succeed"), HttpStatusCode.NotFound, "Payment.AttemptNotFound");
        await AssertProblemAsync(await CompleteRawAsync(victim, otherOrder.OrderId, started.Payment.AttemptId, started.Payment.SimulatorToken, "succeed"), HttpStatusCode.NotFound, "Payment.AttemptNotFound");
        await AssertProblemAsync(await CompleteRawAsync(victim, Guid.NewGuid(), started.Payment.AttemptId, started.Payment.SimulatorToken, "succeed"), HttpStatusCode.NotFound, "Payment.AttemptNotFound");
        await AssertProblemAsync(await CompleteRawAsync(victim, started.OrderId, started.Payment.AttemptId, null, "succeed"), HttpStatusCode.NotFound, "Payment.AttemptNotFound");
        // even with every correct identifier, a different customer cannot complete it
        await AssertProblemAsync(await CompleteRawAsync(attacker, started.OrderId, started.Payment.AttemptId, started.Payment.SimulatorToken, "succeed"), HttpStatusCode.NotFound, "Payment.AttemptNotFound");
        (await CompleteRawAsync(victim, started.OrderId, started.Payment.AttemptId, started.Payment.SimulatorToken, "maybe")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("PendingPayment");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{started.Payment.AttemptId}'")).Should().Be("Initiated");
    }

    [Fact]
    public async Task PaymentInitiation_IsIdempotent_AndOnlyOneAttemptIsInFlight()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var user = await NewUserAsync();
        var order = await CheckoutOkAsync(user, await HoldAsync(user, sale, 0));

        var first = await PayAsync(user, order.OrderId, "pay-key");
        var replay = await PayAsync(user, order.OrderId, "pay-key");
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.Headers.GetValues("Idempotent-Replayed").Should().Equal("true");
        (await replay.Content.ReadFromJsonAsync<OrderResponse>())!.Payment!.AttemptId
            .Should().Be((await first.Content.ReadFromJsonAsync<OrderResponse>())!.Payment!.AttemptId);

        await AssertProblemAsync(await PayAsync(user, order.OrderId, "different-key"), HttpStatusCode.Conflict, "Payment.AttemptInProgress");
        await AssertProblemAsync(await user.Client.SendAsync(Post($"/api/orders/{order.OrderId}/payments", null, null)), HttpStatusCode.BadRequest, "Order.MissingIdempotencyKey");
        (await ScalarAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentPaymentInitiations_WithTheSameKey_CreateOneAttempt()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var user = await NewUserAsync();
        var order = await CheckoutOkAsync(user, await HoldAsync(user, sale, 0));

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PayAsync(user, order.OrderId, "one-key")));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        var tokens = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<OrderResponse>())!.Payment!.SimulatorToken));
        tokens.Distinct().Should().ContainSingle("every caller sees the stored provider reference");
        (await ScalarAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be(1);
    }

    [Fact]
    public async Task CompletionRecovery_AfterACrashBetweenRecordingAndSelling_FinishesOnTheNextCallback()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var user = await NewUserAsync();
        var started = await PayOkAsync(user, (await CheckoutOkAsync(user, await HoldAsync(user, sale, 0))).OrderId);
        // The verdict was durably recorded, then the process died before the sale was confirmed.
        await ExecAsync($"UPDATE payments.payment_attempts SET status = 'Succeeded', completed_at_utc = now() WHERE id = '{started.Payment!.AttemptId}'");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("PendingPayment");

        var retried = await CompleteOkAsync(user, started, succeed: true);

        retried.Status.Should().Be("Paid");
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{started.ReservationId}'")).Should().Be("Confirmed");
    }

    [Fact]
    public async Task Orders_SurviveAProcessRestart()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var user = await NewUserAsync();
        var started = await PayOkAsync(user, (await CheckoutOkAsync(user, await HoldAsync(user, sale, 0))).OrderId);

        // A brand new host (empty memory, new cookie keys) against the same database.
        var restarted = new CatalogApiFactory(_database);
        _disposables.Add(restarted);
        var client = restarted.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(client);
        var login = await client.PostAsJsonAsync("/api/identity/login", new { email = user.Email, password = AuthTestSupport.Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        AuthTestSupport.ApplyToken(client, await login.Content.ReadFromJsonAsync<JsonElement>());
        var again = new TestUser(client, user.Id, user.Email);

        var read = (await (await client.GetAsync($"/api/orders/{started.OrderId}")).Content.ReadFromJsonAsync<OrderResponse>())!;
        read.Status.Should().Be("PendingPayment");
        read.Payment!.SimulatorToken.Should().Be(started.Payment!.SimulatorToken);
        (await CompleteOkAsync(again, read, succeed: true)).Status.Should().Be("Paid");
    }

    // ───────────── expiry during payment (real database-clock expiry) ─────────────

    [Fact]
    public async Task LateSuccess_AfterTheHoldExpired_NeverConfirmsSeats_AndNeverTouchesTheNextBuyer()
    {
        using var shortHold = new CatalogApiFactory(_database, new Dictionary<string, string?> { ["Reservation:HoldDurationSeconds"] = "3" });
        var admin = await AdminTestSupport.RegisterAdminAsync(shortHold);
        var sale = await OnSaleEventBuilder.CreateAsync(admin.Client, seatCount: 1);
        var slow = await AuthTestSupport.RegisterAsync(shortHold);
        var next = await AuthTestSupport.RegisterAsync(shortHold);

        var reservation = await HoldAsync(slow, sale, 0);
        var order = await CheckoutOkAsync(slow, reservation);
        var started = await PayOkAsync(slow, order.OrderId);

        await Task.Delay(3500); // the database clock passes the original expiry while the payment is "in flight"

        // Another customer buys the seat first and is confirmed...
        var nextReservation = await HoldAsync(next, sale, 0);
        var nextOrder = await CheckoutOkAsync(next, nextReservation);
        var nextStarted = await PayOkAsync(next, nextOrder.OrderId);

        // ...then the slow customer's provider reports success (a late callback), racing the next buyer's payment.
        var lateTask = CompleteAsync(slow, started, "succeed");
        var nextTask = CompleteAsync(next, nextStarted, "succeed");
        var late = await lateTask;
        var nextDone = await nextTask;

        late.StatusCode.Should().Be(HttpStatusCode.OK);
        (await late.Content.ReadFromJsonAsync<OrderResponse>())!.Status.Should().Be("RefundRequired");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{started.Payment!.AttemptId}'")).Should().Be("Succeeded");
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{reservation.ReservationId}'")).Should().Be("Expired");

        nextDone.StatusCode.Should().Be(HttpStatusCode.OK);
        (await nextDone.Content.ReadFromJsonAsync<OrderResponse>())!.Status.Should().Be("Paid");
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Sold' AND reservation_id = '{nextReservation.ReservationId}'")).Should().Be(1);
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND reservation_id = '{reservation.ReservationId}'")).Should().Be(0);
    }

    [Fact]
    public async Task LateSuccess_WhenNobodyElseBoughtTheSeat_StillEndsAsRefundRequired()
    {
        using var shortHold = new CatalogApiFactory(_database, new Dictionary<string, string?> { ["Reservation:HoldDurationSeconds"] = "3" });
        var admin = await AdminTestSupport.RegisterAdminAsync(shortHold);
        var sale = await OnSaleEventBuilder.CreateAsync(admin.Client, seatCount: 1);
        var user = await AuthTestSupport.RegisterAsync(shortHold);
        var order = await CheckoutOkAsync(user, await HoldAsync(user, sale, 0));
        var started = await PayOkAsync(user, order.OrderId);

        await Task.Delay(3500);

        var read = (await (await user.Client.GetAsync($"/api/orders/{order.OrderId}")).Content.ReadFromJsonAsync<OrderResponse>())!;
        read.Status.Should().Be("Expired", "an overdue order is reported as expired by the database clock");
        await AssertProblemAsync(await PayAsync(user, order.OrderId), HttpStatusCode.Conflict, "Order.NotPayable");

        var done = await CompleteOkAsync(user, started, succeed: true);

        done.Status.Should().Be("RefundRequired", "a simple, deterministic rule: nothing is sold once the hold expired, even if the seat is still free");
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Sold'")).Should().Be(0);
    }

    [Fact]
    public async Task LateSuccess_AfterTheOrderWasCancelled_IsRecordedAsRefundRequired()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var user = await NewUserAsync();
        var order = await CheckoutOkAsync(user, await HoldAsync(user, sale, 0));
        var started = await PayOkAsync(user, order.OrderId);
        (await user.Client.PostAsync($"/api/orders/{order.OrderId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{started.Payment!.AttemptId}'")).Should().Be("Cancelled");

        var done = await CompleteOkAsync(user, started, succeed: true);

        done.Status.Should().Be("RefundRequired");
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Sold'")).Should().Be(0);
    }

    [Fact]
    public async Task TwoCustomersRacingForOneSeat_OnlyOneCanHoldAndCheckOut()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var users = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => NewUserAsync()));

        var holds = await Task.WhenAll(users.Select(u => HoldRawAsync(u, sale, 0)));
        holds.Count(h => h.StatusCode == HttpStatusCode.Created).Should().Be(1);
        var winnerIndex = Array.FindIndex(holds, h => h.StatusCode == HttpStatusCode.Created);
        var loserIndex = 1 - winnerIndex;
        var winnerReservation = (await holds[winnerIndex].Content.ReadFromJsonAsync<ReservationResponse>())!;

        // The loser cannot check out the winner's reservation, nor the winner's seats.
        await AssertProblemAsync(await CheckoutAsync(users[loserIndex], winnerReservation.ReservationId), HttpStatusCode.NotFound, "Order.ReservationNotFound");
        var order = await CheckoutOkAsync(users[winnerIndex], winnerReservation);
        var paid = await CompleteOkAsync(users[winnerIndex], await PayOkAsync(users[winnerIndex], order.OrderId), succeed: true);

        paid.Status.Should().Be("Paid");
        (await ScalarAsync($"SELECT count(*) FROM payments.orders o JOIN reservation.reservations r ON r.id = o.reservation_id WHERE r.event_id = '{sale.EventId}'")).Should().Be(1);
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Sold'")).Should().Be(1);
    }

    // ───────────── simulator safeguards ─────────────

    [Fact]
    public async Task WithTheSimulatorDisabled_NoPaymentCanBeStarted_AndTheSimulatorRouteDoesNotExist()
    {
        using var disabled = new CatalogApiFactory(_database, new Dictionary<string, string?> { ["Payments:Simulator:Enabled"] = "false" });
        var admin = await AdminTestSupport.RegisterAdminAsync(disabled);
        var sale = await OnSaleEventBuilder.CreateAsync(admin.Client, seatCount: 1);
        var user = await AuthTestSupport.RegisterAsync(disabled);
        var reservation = await HoldAsync(user, sale, 0);
        var order = await CheckoutOkAsync(user, reservation); // orders can exist, but nothing can take payment

        await AssertProblemAsync(await PayAsync(user, order.OrderId), HttpStatusCode.ServiceUnavailable, "Payment.ProviderUnavailable");
        var route = await user.Client.SendAsync(Post("/api/dev/payment-simulator/complete", new { orderId = order.OrderId, attemptId = Guid.NewGuid(), simulatorToken = "x", outcome = "succeed" }, null));
        route.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ScalarAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be(0);
    }

    // ───────────── helpers ─────────────

    private Task<TestUser> NewUserAsync() => AuthTestSupport.RegisterAsync(_factory);

    private static HttpRequestMessage Post(string path, object? body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static Task<HttpResponseMessage> HoldRawAsync(TestUser user, OnSaleEventBuilder.SaleSetup sale, params int[] seatIndexes) =>
        user.Client.SendAsync(Post("/api/reservations", new { eventId = sale.EventId, seatIds = seatIndexes.Select(i => sale.SeatIds[i]).ToArray() }, Guid.NewGuid().ToString()));

    private static async Task<ReservationResponse> HoldAsync(TestUser user, OnSaleEventBuilder.SaleSetup sale, params int[] seatIndexes)
    {
        var response = await HoldRawAsync(user, sale, seatIndexes);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReservationResponse>())!;
    }

    private static Task<HttpResponseMessage> CheckoutAsync(TestUser user, Guid reservationId, string? key = "") =>
        user.Client.SendAsync(Post("/api/orders", new { reservationId }, key is "" ? Guid.NewGuid().ToString() : key));

    private static async Task<OrderResponse> CheckoutOkAsync(TestUser user, ReservationResponse reservation)
    {
        var response = await CheckoutAsync(user, reservation.ReservationId);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private static Task<HttpResponseMessage> PayAsync(TestUser user, Guid orderId, string? key = "") =>
        user.Client.SendAsync(Post($"/api/orders/{orderId}/payments", null, key is "" ? Guid.NewGuid().ToString() : key));

    private static async Task<OrderResponse> PayOkAsync(TestUser user, Guid orderId)
    {
        var response = await PayAsync(user, orderId);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private static Task<HttpResponseMessage> CompleteRawAsync(TestUser user, Guid orderId, Guid attemptId, string? token, string outcome) =>
        user.Client.SendAsync(Post("/api/dev/payment-simulator/complete", new { orderId, attemptId, simulatorToken = token, outcome }, null));

    private static Task<HttpResponseMessage> CompleteAsync(TestUser user, OrderResponse started, string outcome) =>
        CompleteRawAsync(user, started.OrderId, started.Payment!.AttemptId, started.Payment.SimulatorToken, outcome);

    private static async Task<OrderResponse> CompleteOkAsync(TestUser user, OrderResponse started, bool succeed)
    {
        var response = await CompleteAsync(user, started, succeed ? "succeed" : "fail");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private async Task<List<JsonElement>> AvailabilityAsync(Guid eventId)
    {
        var json = await _admin.GetFromJsonAsync<JsonElement>($"/api/events/{eventId}/availability");
        return json.GetProperty("seats").EnumerateArray().ToList();
    }

    private async Task ExpireReservationAsync(Guid reservationId)
    {
        await ExecAsync($"UPDATE reservation.reservations SET created_at_utc = now() - interval '20 minutes', expires_at_utc = now() - interval '10 minutes' WHERE id = '{reservationId}'");
        await ExecAsync($"UPDATE reservation.event_seats SET hold_expires_at_utc = now() - interval '10 minutes' WHERE reservation_id = '{reservationId}'");
    }

    private async Task ExecAsync(string sql)
    {
        await using var db = _database.CreateReservationDbContext();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql) => Convert.ToInt64(await ScalarObjectAsync(sql));

    private async Task<string?> StringAsync(string sql) => (await ScalarObjectAsync(sql))?.ToString();

    private async Task<object?> ScalarObjectAsync(string sql)
    {
        await using var db = _database.CreateReservationDbContext();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
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

public class PaymentsConfigurationTests
{
    private sealed class Env(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Config(string? value) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : [new KeyValuePair<string, string?>(PulseReserve.AppHost.Configuration.PaymentsOptions.SimulatorKey, value)])
            .Build();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void SimulatorEnabled_OutsideDevelopmentAndTesting_FailsStartup(string environment)
    {
        var act = () => PulseReserve.AppHost.Configuration.PaymentsOptions.Resolve(Config("true"), new Env(environment));
        act.Should().Throw<InvalidOperationException>().WithMessage("*simulator*");
    }

    [Fact]
    public void SimulatorEnabled_InDevelopment_IsAllowed() =>
        PulseReserve.AppHost.Configuration.PaymentsOptions.Resolve(Config("true"), new Env("Development")).SimulatorEnabled.Should().BeTrue();

    [Fact]
    public void SimulatorEnabled_InTesting_NeedsTheExplicitOptIn()
    {
        var withoutOptIn = () => PulseReserve.AppHost.Configuration.PaymentsOptions.Resolve(Config("true"), new Env("Testing"));
        withoutOptIn.Should().Throw<InvalidOperationException>().WithMessage("*AllowInTestingEnvironment*");

        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PulseReserve.AppHost.Configuration.PaymentsOptions.SimulatorKey] = "true",
            [PulseReserve.AppHost.Configuration.PaymentsOptions.SimulatorTestingOptInKey] = "true"
        }).Build();
        PulseReserve.AppHost.Configuration.PaymentsOptions.Resolve(configuration, new Env("Testing")).SimulatorEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void SimulatorIsOffByDefault_EverywhereIncludingProduction(string? value) =>
        PulseReserve.AppHost.Configuration.PaymentsOptions.Resolve(Config(value), new Env("Production")).SimulatorEnabled.Should().BeFalse();

    [Fact]
    public void AnUnparsableValue_FailsStartup_InsteadOfSilentlyDisabling()
    {
        var act = () => PulseReserve.AppHost.Configuration.PaymentsOptions.Resolve(Config("yes please"), new Env("Production"));
        act.Should().Throw<InvalidOperationException>();
    }
}
