using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Npgsql;
using PulseReserve.AppHost.Api;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

// Races and failures that need a real PostgreSQL (Docker). They are compiled but have NOT been executed in this working tree.
//
// How contention is made observable: PostgresRace.RaceBehindLockAsync holds a row lock on a separate connection, starts each
// caller only after PostgreSQL reports one more session waiting on a lock, then releases it. That guarantees every caller is in
// flight and queued behind the same row; it does NOT choose which waiter PostgreSQL wakes first. So these tests assert the
// invariants that must hold for EITHER order and read the persisted rows; they never claim a particular interleaving. Where a
// scenario has a single deterministic order it is written sequentially instead.
public sealed partial class ProviderPaymentsHttpTests
{
    private string ConnectionString => _database.ConnectionString;

    private string LockOrder(OrderResponse order) => $"SELECT 1 FROM payments.orders WHERE id = '{order.OrderId}' FOR UPDATE";

    private static async Task<HttpStatusCode> StatusAsync(Func<Task<HttpResponseMessage>> send)
    {
        // With no exception handler a failure inside the pipeline can surface as an exception from the test server instead of a
        // 500; either way the request did not succeed, which is what a webhook sender must see.
        try
        {
            using var response = await send();
            return response.StatusCode;
        }
        catch (Exception)
        {
            return HttpStatusCode.InternalServerError;
        }
    }

    [Fact]
    public async Task PaymentStart_RacingALateSuccess_NeverLeavesAnInFlightAttemptBesideAnException()
    {
        for (var round = 0; round < 2; round++)
        {
            var (w, user, order, started, attemptId, session) = await FailedAttemptAsync();

            var results = await PostgresRace.RaceBehindLockAsync(ConnectionString, LockOrder(order),
                () => StatusAsync(() => StartPaymentAsync(user, order.OrderId, "pay-race", null)),
                () => StatusAsync(() => LateSuccessAsync(w, attemptId, order, started, session)));
            var start = results[0];
            var hook = results[1];

            hook.Should().Be(HttpStatusCode.OK);
            start.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
            (await CountAsync($"SELECT count(*) FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'")).Should().Be(1);
            (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Failed");
            (await CountAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}' AND status = 'Initiated'"))
                .Should().Be(0, $"round {round}: whichever won, no payable attempt may sit beside an exception");
            if (start == HttpStatusCode.Created)
            {
                // The start won the lock: its attempt was created first and the exception then withdrew it.
                (await CountAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be(2);
                (await StringAsync($"SELECT status || '|' || COALESCE(failure_code, '') || '|' || (checkout_url IS NULL) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}' AND id <> '{attemptId}'"))
                    .Should().Be("Cancelled|order_held_for_review|true", "the withdrawn attempt keeps no payment link");
            }
            else
            {
                // The exception won: the start was refused and no second attempt exists.
                (await CountAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be(1);
            }

            (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");
            (await CountAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be(0);
            (await CountAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be(0);
        }
    }

    [Fact]
    public async Task ResolutionInFlight_AndARepeatedLateSuccess_LeaveOneResolvedException_AndOneAuditRow()
    {
        var (w, _, order, started, attemptId, session) = await FailedAttemptAsync();
        (await LateSuccessAsync(w, attemptId, order, started, session)).StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await StringAsync($"SELECT id FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'"))!;

        // A resolution is queued behind a lock on the exception row (it is provably waiting) while the provider redelivers.
        await using var held = await RowLock.AcquireAsync(ConnectionString, $"SELECT 1 FROM payments.payment_exceptions WHERE id = '{id}' FOR UPDATE");
        var resolve = Task.Run(() => w.Admin.Client.PostAsJsonAsync($"/api/admin/payments/exceptions/{id}/resolve",
            new { refundPerformedExternally = true, amount = started.TotalAmount, currency = started.Currency, externalReference = "re_race" }));
        await PostgresRace.WaitForBlockedSessionsAsync(ConnectionString, 1);

        var redelivery = await LateSuccessAsync(w, attemptId, order, started, session, eventId: "evt_it_redelivered");
        redelivery.StatusCode.Should().Be(HttpStatusCode.OK, "a redelivery is acknowledged while the resolution waits; it must not block on the locked row");
        (await StringAsync($"SELECT state FROM payments.payment_exceptions WHERE id = '{id}'")).Should().Be("Open", "the resolution has not committed yet");

        await held.ReleaseAsync();
        (await resolve.WaitAsync(PostgresRace.Timeout)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await LateSuccessAsync(w, attemptId, order, started, session, eventId: "evt_it_after")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync($"SELECT count(*) FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'")).Should().Be(1);
        (await StringAsync($"SELECT state || '|' || resolution_reference || '|' || provider_event_id FROM payments.payment_exceptions WHERE id = '{id}'"))
            .Should().Be("Resolved|re_race|evt_it_late", "the first verified event stays on record and later deliveries change nothing");
        (await CountAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{id}' AND action = 'ResolvePaymentException'")).Should().Be(1);
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Failed");
    }

    [Fact]
    public async Task WebhookRedelivery_AfterAForcedDatabaseFailure_IsRecordedOnTheRetry_AndNeverAcknowledgedBefore()
    {
        var (w, _, order, started, attemptId, session) = await FailedAttemptAsync();

        // Force the exception insert (and so the whole verdict transaction) to fail, deterministically, in the test database.
        await ExecAsync("CREATE FUNCTION payments.test_force_failure() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'forced test failure'; END; $$ LANGUAGE plpgsql");
        await ExecAsync("CREATE TRIGGER trg_test_force_failure BEFORE INSERT ON payments.payment_exceptions FOR EACH ROW EXECUTE FUNCTION payments.test_force_failure()");
        HttpStatusCode first;
        try
        {
            first = await StatusAsync(() => LateSuccessAsync(w, attemptId, order, started, session, eventId: "evt_it_redeliver"));
        }
        finally
        {
            await ExecAsync("DROP TRIGGER IF EXISTS trg_test_force_failure ON payments.payment_exceptions");
            await ExecAsync("DROP FUNCTION IF EXISTS payments.test_force_failure()");
        }

        ((int)first).Should().BeGreaterThanOrEqualTo(500, "a verified success that could not be recorded must not be acknowledged, so the provider redelivers it");
        (await CountAsync($"SELECT count(*) FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'")).Should().Be(0, "the failed transaction left nothing behind");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Failed");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");

        var redelivered = await LateSuccessAsync(w, attemptId, order, started, session, eventId: "evt_it_redeliver");
        redelivered.StatusCode.Should().Be(HttpStatusCode.OK);
        (await redelivered.Content.ReadAsStringAsync()).Should().Contain("late_success_recorded_for_review");
        (await StringAsync($"SELECT state || '|' || provider_event_id FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'")).Should().Be("Open|evt_it_redeliver");
    }

    [Fact]
    public async Task SuccessAndExpiredEvent_BothContendingForTheOrder_LeaveOneConsistentVerdict_AndNothingIsLost()
    {
        var w = NewWorld();
        StripeBehaves(w);
        for (var round = 0; round < 2; round++)
        {
            var (user, _, order) = await OrderAsync(w);
            var started = await StartOkAsync(user, order.OrderId, $"pay-{round}", null);
            var attemptId = started.Payment!.AttemptId.ToString();
            var session = $"cs_test_{attemptId[..8]}";

            var codes = await PostgresRace.RaceBehindLockAsync(ConnectionString, LockOrder(order),
                () => StatusAsync(() => w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, "checkout.session.expired", "unpaid", $"evt_exp_{round}"))),
                () => StatusAsync(() => w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, eventId: $"evt_paid_{round}"))));
            await RunWorkflowAsync(w);

            codes.Should().OnlyContain(c => c == HttpStatusCode.OK, "the losing event is acknowledged, never an error");
            var attempt = await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'");
            var exceptions = await CountAsync($"SELECT count(*) FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'");
            if (attempt == "Failed")
            {
                // The failure won. The verified success is not lost: it is an exception, and nothing was fulfilled.
                exceptions.Should().Be(1, $"round {round}");
                (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");
                (await CountAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be(0);
                (await CountAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{order.OrderId}'")).Should().Be(0);
            }
            else
            {
                attempt.Should().Be("Succeeded");
                exceptions.Should().Be(0, $"round {round}: the success won, so the later failure is only acknowledged");
                (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Paid");
                (await CountAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be(1);
            }
        }
    }

    [Fact]
    public async Task LateSuccess_ForAReleasedReservation_IsRecorded_ButNeverSoldTicketedOrRefundedBySystem()
    {
        var (w, user, order, started, attemptId, session) = await FailedAttemptAsync();
        await ExecAsync($"UPDATE reservation.event_seats SET status = 'Available', reservation_id = NULL, hold_expires_at_utc = NULL WHERE reservation_id = '{order.ReservationId}'");
        await ExecAsync($"UPDATE reservation.reservations SET status = 'Released', closed_at_utc = now() WHERE id = '{order.ReservationId}'");

        (await LateSuccessAsync(w, attemptId, order, started, session)).StatusCode.Should().Be(HttpStatusCode.OK);
        await RunWorkflowAsync(w);

        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{order.ReservationId}'")).Should().Be("Released");
        (await StringAsync($"SELECT state FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'")).Should().Be("Open");
        (await CountAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{order.OrderId}'")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be(0);
        user.Should().NotBeNull();
    }

    [Fact]
    public async Task ARejectedPaymentStart_AfterTheException_LeavesNoNewAttemptAndNoProviderCall()
    {
        var (w, user, order, started, attemptId, session) = await FailedAttemptAsync();
        await LateSuccessAsync(w, attemptId, order, started, session);
        var providerCalls = w.StripeHttp.Requests.Count;

        foreach (var provider in new string?[] { null, "Stripe", "Paymob" })
        {
            var response = await StartPaymentAsync(user, order.OrderId, "pay-blocked-" + provider, provider);
            new[] { HttpStatusCode.Conflict, HttpStatusCode.BadRequest }.Should().Contain(response.StatusCode, "no provider fallback may start a payment");
        }

        (await CountAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be(1);
        w.StripeHttp.Requests.Count.Should().Be(providerCalls, "no provider was contacted for a blocked order");
    }
}
