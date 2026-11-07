using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Npgsql;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

// Financial transition history, payment anomalies and per-order eligibility, against a real PostgreSQL (Docker).
// COMPILED, NOT EXECUTED in this working tree. The history rows are written by a database trigger in the same transaction as the
// change, so the code-level tests (TransitionContextTests, TransitionLabellingTests) cannot establish them; these can.
public sealed partial class ProviderPaymentsHttpTests
{
    private async Task<List<string>> HistoryAsync(string where)
    {
        await using var db = _database.CreateReservationDbContext();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText =
            "SELECT entity_type || '|' || COALESCE(from_state, 'new') || '|' || to_state || '|' || kind || '|' || source || '|' || fact_basis || '|' || " +
            "COALESCE(provider, '') || '|' || COALESCE(provider_event_id, '') || '|' || COALESCE(reason, '') " +
            $"FROM payments.financial_transitions WHERE {where} ORDER BY occurred_at_utc, id";
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    [Fact]
    public async Task TransitionHistory_RecordsTheWholeLifeOfAWebhookPayment_WithSourceBasisProviderAndEvent()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        var session = $"cs_test_{attemptId[..8]}";

        (await w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, eventId: "evt_hist_a"))).StatusCode.Should().Be(HttpStatusCode.OK);
        await RunWorkflowAsync(w);

        (await HistoryAsync($"order_id = '{order.OrderId}'")).Should().BeEquivalentTo(
        [
            "Order|new|PendingPayment|Transition|Customer|ApplicationDecision|||",
            "PaymentAttempt|new|Initiated|Transition|Customer|ApplicationDecision|Stripe||",
            "PaymentAttempt|Initiated|Succeeded|Transition|ProviderWebhook|ProviderVerified|Stripe|evt_hist_a|provider_verdict",
            "Order|PendingPayment|Paid|Transition|ProviderWebhook|ApplicationDecision||evt_hist_a|sale_confirmation"
        ]);
        (await CountAsync($"SELECT count(DISTINCT correlation_id) FROM payments.financial_transitions WHERE provider_event_id = 'evt_hist_a'")).Should().Be(1, "one webhook is one correlation id");
    }

    [Fact]
    public async Task TransitionHistory_IsNotDuplicated_ByRedeliveredEvents()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        var session = $"cs_test_{attemptId[..8]}";

        for (var i = 0; i < 4; i++)
            (await w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, eventId: "evt_hist_dup"))).StatusCode.Should().Be(HttpStatusCode.OK);
        await RunWorkflowAsync(w);
        await RunWorkflowAsync(w);

        (await CountAsync($"SELECT count(*) FROM payments.financial_transitions WHERE order_id = '{order.OrderId}'")).Should().Be(4, "a delivery that changes nothing writes nothing");
    }

    [Fact]
    public async Task TransitionHistory_LeavesNothingForAChangeThatRolledBack_AndRecordsTheRecoveryAsAWorkerDecision()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        var session = $"cs_test_{attemptId[..8]}";

        // Make the order's move to Paid fail inside its transaction (the verdict is already committed in its own transaction).
        await ExecAsync("CREATE FUNCTION payments.test_block_paid() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'forced test failure'; END; $$ LANGUAGE plpgsql");
        await ExecAsync("CREATE TRIGGER trg_test_block_paid BEFORE UPDATE OF status ON payments.orders FOR EACH ROW WHEN (NEW.status = 'Paid') EXECUTE FUNCTION payments.test_block_paid()");
        HttpStatusCode first;
        try
        {
            first = await StatusAsync(() => w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, eventId: "evt_hist_rb")));
        }
        finally
        {
            await ExecAsync("DROP TRIGGER IF EXISTS trg_test_block_paid ON payments.orders");
            await ExecAsync("DROP FUNCTION IF EXISTS payments.test_block_paid()");
        }

        ((int)first).Should().BeGreaterThanOrEqualTo(500);
        (await HistoryAsync($"order_id = '{order.OrderId}' AND to_state = 'Paid'")).Should().BeEmpty("the order never became Paid, so no history says it did");
        (await HistoryAsync($"entity_type = 'PaymentAttempt' AND order_id = '{order.OrderId}' AND to_state = 'Succeeded'")).Should().ContainSingle("the committed verdict is on record");

        // Recovery: the worker finishes the sale later, and the history says it was the worker, not the webhook.
        await ExecAsync($"UPDATE payments.payment_attempts SET completed_at_utc = now() - interval '5 minutes' WHERE id = '{attemptId}'");
        await RunWorkflowAsync(w);
        await RunWorkflowAsync(w);
        (await HistoryAsync($"order_id = '{order.OrderId}' AND to_state = 'Paid'")).Should().ContainSingle().Which.Should().StartWith("Order|PendingPayment|Paid|Transition|Worker|ApplicationDecision");
    }

    [Fact]
    public async Task TransitionHistory_IsAppendOnly_AndCarriesNoSecretsOrPayloads()
    {
        var (w, _, order, started, attemptId, session) = await FailedAttemptAsync();
        await LateSuccessAsync(w, attemptId, order, started, session);

        var rewrite = () => ExecAsync("UPDATE payments.financial_transitions SET reason = 'edited'");
        await rewrite.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        var erase = () => ExecAsync("DELETE FROM payments.financial_transitions");
        await erase.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");

        (await CountAsync("SELECT count(*) FROM payments.financial_transitions t WHERE row_to_json(t)::text ~* 'whsec|sk_test|signature|egy_|cs_test_|client_secret|hmac'")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM payments.financial_transitions WHERE entity_type = 'PaymentException' AND source = 'ProviderWebhook' AND fact_basis = 'ProviderVerified' AND reason = 'late_success_recorded' AND provider_event_id = 'evt_it_late'")).Should().Be(1);
    }

    [Fact]
    public async Task TransitionHistory_KeepsAConflictingEventOnRecord_OncePerProviderEvent()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        var session = $"cs_test_{attemptId[..8]}";
        await w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, eventId: "evt_paid"));

        for (var i = 0; i < 3; i++)
            (await w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, "checkout.session.expired", "unpaid", "evt_expired_after_paid"))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await HistoryAsync($"kind = 'Rejected' AND order_id = '{order.OrderId}'")).Should().ContainSingle().Which
            .Should().Be("PaymentAttempt|Succeeded|Failed|Rejected|ProviderWebhook|ProviderVerified|Stripe|evt_expired_after_paid|conflicting_provider_verdict");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Succeeded");
    }

    // ───────────── amount-mismatch evidence ─────────────

    [Fact]
    public async Task AMismatchingEvent_IsKeptAsEvidence_AppliesNothing_AndIsRecordedOncePerProviderEvent()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        var session = $"cs_test_{attemptId[..8]}";

        // Acknowledged (2xx) because the evidence is committed; a 4xx would make the provider retry an event that is already on record.
        for (var i = 0; i < 3; i++)
            (await w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, 100, started.Currency, eventId: "evt_mismatch"))).StatusCode.Should().Be(HttpStatusCode.OK);
        await RunWorkflowAsync(w);

        (await StringAsync($"SELECT kind || '|' || reason_code || '|' || state || '|' || provider || '|' || received_amount_minor || '|' || received_currency || '|' || expected_amount_minor || '|' || expected_currency || '|' || (attempt_id = '{attemptId}') || '|' || (order_id = '{order.OrderId}') FROM payments.payment_anomalies"))
            .Should().Be($"AmountOrCurrencyMismatch|amount_or_currency_mismatch|Open|Stripe|100|{started.Currency}|{Minor(started)}|{started.Currency}|true|true");
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies")).Should().Be(1, "one anomaly per provider event");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Initiated");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");
        (await CountAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{order.OrderId}'")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be(0);
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{order.ReservationId}'")).Should().NotBe("Confirmed");
        (await HistoryAsync("entity_type = 'PaymentAnomaly'")).Should().ContainSingle().Which.Should().Be("PaymentAnomaly|new|Open|Transition|ProviderWebhook|ProviderVerified|Stripe|evt_mismatch|payment_anomaly_recorded");
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies a WHERE row_to_json(a)::text ~* 'whsec|sk_test|signature|egy_|cs_test_|client_secret|hmac'")).Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentIdenticalMismatchWebhooks_AreAllAcknowledged_AndLeaveOneRecordAndOneHistoryRow()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        var session = $"cs_test_{attemptId[..8]}";

        // The same wrong-amount event delivered 8 times at once (a provider retry storm), each on its own connection. This is a
        // start-together test, not a lock-barrier test: the interleaving is not controlled, so only what must hold for EVERY order is
        // asserted (all acknowledged, one record, one history row). The unique index and ON CONFLICT arbitrate the insert.
        var statuses = await RaceAsync(8, async _ => (await w.Host.CreateClient().SendAsync(
            StripeWebhook(attemptId, order.OrderId.ToString(), session, 100, started.Currency, eventId: "evt_mismatch_storm"))).StatusCode);
        await RunWorkflowAsync(w);

        statuses.Should().OnlyContain(c => c == HttpStatusCode.OK, "every delivery is acknowledged once the record exists; none is an error");
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies")).Should().Be(1);
        (await HistoryAsync("entity_type = 'PaymentAnomaly'")).Should().ContainSingle();
        (await StringAsync($"SELECT state FROM payments.payment_anomalies")).Should().Be("Open", "no worker pass resolves or removes an anomaly");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");
        (await CountAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be(0);
    }

    [Fact]
    public async Task AnAnomalyInsertThatMatchesNoRow_IsNeverReportedAsADuplicate_AndARealDuplicateIsConfirmed()
    {
        var w = NewWorld();
        await using var scope = w.Host.Services.CreateAsyncScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IPaymentAnomalyRecorder>();

        // A correlated record that names an attempt and order which do not exist: the insert selects nothing and stores nothing. That
        // looks exactly like a duplicate to ON CONFLICT DO NOTHING (zero rows), so the store must check that a record really exists
        // and refuse, otherwise the webhook would be acknowledged without any evidence.
        var ghost = new PaymentAnomalyRecord(
            PaymentAnomalyKind.AmountOrCurrencyMismatch, "amount_or_currency_mismatch", "Stripe", "evt_ghost",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100, "USD", 9100, "USD");
        var act = () => recorder.RecordAsync(ghost);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*neither stored nor already on record*");
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies")).Should().Be(0);

        // A real duplicate: stored once, then confirmed to exist and reported as a duplicate (false), without a second row.
        var unknown = new PaymentAnomalyRecord(
            PaymentAnomalyKind.UnknownPaymentReference, "unknown_attempt", "Stripe", "evt_dup_confirm", null, null, Guid.NewGuid(), null, 100, "USD", null, null);
        (await recorder.RecordAsync(unknown)).Should().BeTrue();
        (await recorder.RecordAsync(unknown)).Should().BeFalse();
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies")).Should().Be(1);
    }

    [Fact]
    public async Task AnEventForAnUnknownAttempt_IsKeptWithoutAnyLinkToAnOrderOrAttempt_AndAForgedOneIsNot()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (_, _, order) = await OrderAsync(w);
        var phantom = Guid.NewGuid().ToString();

        (await w.Host.CreateClient().SendAsync(StripeWebhook(phantom, order.OrderId.ToString(), "cs_test_phantom", 9100, "USD", eventId: "evt_unknown", secret: "whsec_not_ours"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies")).Should().Be(0, "a forged event creates nothing");

        (await w.Host.CreateClient().SendAsync(StripeWebhook(phantom, order.OrderId.ToString(), "cs_test_phantom", 9100, "USD", eventId: "evt_unknown"))).StatusCode.Should().Be(HttpStatusCode.OK, "authentic and now on record");

        (await StringAsync($"SELECT kind || '|' || (attempt_id IS NULL) || '|' || (order_id IS NULL) || '|' || (claimed_attempt_id = '{phantom}') FROM payments.payment_anomalies"))
            .Should().Be("UnknownPaymentReference|true|true|true");
    }

    [Fact]
    public async Task AMismatchWebhook_IsNotAcknowledgedWhenTheEvidenceCannotBeStored_AndIsRecordedOnRedelivery()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        var session = $"cs_test_{attemptId[..8]}";

        await ExecAsync("CREATE FUNCTION payments.test_block_anomaly() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'forced test failure'; END; $$ LANGUAGE plpgsql");
        await ExecAsync("CREATE TRIGGER trg_test_block_anomaly BEFORE INSERT ON payments.payment_anomalies FOR EACH ROW EXECUTE FUNCTION payments.test_block_anomaly()");
        HttpStatusCode first;
        try
        {
            first = await StatusAsync(() => w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, 100, started.Currency, eventId: "evt_mismatch_rb")));
        }
        finally
        {
            await ExecAsync("DROP TRIGGER IF EXISTS trg_test_block_anomaly ON payments.payment_anomalies");
            await ExecAsync("DROP FUNCTION IF EXISTS payments.test_block_anomaly()");
        }

        ((int)first).Should().BeGreaterThanOrEqualTo(500, "storage failure must not look like a normal rejection that the provider would stop retrying");
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies")).Should().Be(0);
        (await w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, 100, started.Currency, eventId: "evt_mismatch_rb"))).StatusCode.Should().Be(HttpStatusCode.OK, "the provider's retry is recorded and only then acknowledged");
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies")).Should().Be(1);
        (await w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, 100, started.Currency, eventId: "evt_mismatch_rb"))).StatusCode.Should().Be(HttpStatusCode.OK, "a further redelivery is a duplicate of a committed record");
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies")).Should().Be(1);
    }

    [Fact]
    public async Task UnresolvedAnomalies_AreCountedFromTheDatabase_AgedOnItsClock_AndTheOldestIsAlwaysListedFirst()
    {
        var w = NewWorld();
        // Inserted directly (the facts of an anomaly are immutable afterwards) so that their detection times differ.
        await ExecAsync(
            "INSERT INTO payments.payment_anomalies (id, kind, reason_code, state, dedupe_key, provider, provider_event_id, claimed_attempt_id, received_amount_minor, received_currency, detected_at_utc) VALUES " +
            "(gen_random_uuid(), 'UnknownPaymentReference', 'unknown_attempt', 'Open', 'Stripe:evt_old', 'Stripe', 'evt_old', gen_random_uuid(), 100, 'USD', now() - interval '3 days'), " +
            "(gen_random_uuid(), 'UnknownPaymentReference', 'unknown_attempt', 'Open', 'Stripe:evt_mid', 'Stripe', 'evt_mid', gen_random_uuid(), 100, 'USD', now() - interval '2 hours'), " +
            "(gen_random_uuid(), 'InconsistentAssociation', 'order_mismatch', 'Open', 'Stripe:evt_new', 'Stripe', 'evt_new', gen_random_uuid(), 100, 'USD', now())");

        var overview = await w.Admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/overview");
        overview.GetProperty("paymentAnomaliesOpen").GetInt32().Should().Be(3);
        overview.GetProperty("paymentAnomaliesOpenByKind").GetProperty("UnknownPaymentReference").GetInt32().Should().Be(2);
        overview.GetProperty("paymentAnomaliesOpenByKind").GetProperty("InconsistentAssociation").GetInt32().Should().Be(1);
        overview.GetProperty("oldestOpenPaymentAnomalyAgeSeconds").GetInt64().Should().BeInRange(3 * 86_400, (3 * 86_400) + 3_600);

        // A page of one is the OLDEST open anomaly, so newer ones can never push a long-waiting one out of view.
        var firstPage = await w.Admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/anomalies?state=Open&limit=1");
        firstPage.GetArrayLength().Should().Be(1);
        firstPage[0].GetProperty("providerEventId").GetString().Should().Be("evt_old");
        firstPage[0].GetProperty("ageSeconds").GetInt64().Should().BeInRange(3 * 86_400, (3 * 86_400) + 3_600);

        // Resolving one lowers the open count but the record stays visible: open ones first (oldest first), then resolved ones.
        var oldId = firstPage[0].GetProperty("anomalyId").GetString();
        (await w.Admin.Client.PostAsJsonAsync($"/api/admin/payments/anomalies/{oldId}/resolve", new { investigationConfirmed = true, resolution = "NotOurPayment" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await w.Admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/overview")).GetProperty("paymentAnomaliesOpen").GetInt32().Should().Be(2);
        var all = await w.Admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/anomalies");
        all.EnumerateArray().Select(a => a.GetProperty("providerEventId").GetString()).Should().Equal("evt_mid", "evt_new", "evt_old");
        all.EnumerateArray().Select(a => a.GetProperty("state").GetString()).Should().Equal("Open", "Open", "Resolved");
    }

    [Fact]
    public async Task AnAnomaly_CanOnlyBeResolvedAsAnAdministratorStatement_WithAudit_AndChangesNothingElse()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        await w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), $"cs_test_{attemptId[..8]}", 100, started.Currency, eventId: "evt_mismatch_r"));
        var id = (await StringAsync("SELECT id FROM payments.payment_anomalies"))!;
        Task<HttpResponseMessage> Resolve(object body) => w.Admin.Client.PostAsJsonAsync($"/api/admin/payments/anomalies/{id}/resolve", body);

        await AssertProblemAsync(await Resolve(new { investigationConfirmed = false, resolution = "NoActionNeeded" }), HttpStatusCode.BadRequest, "Operations.AnomalyNotConfirmed");
        await AssertProblemAsync(await Resolve(new { investigationConfirmed = true, resolution = "MarkOrderPaid" }), HttpStatusCode.BadRequest, "Operations.AnomalyResolutionInvalid");
        (await StringAsync($"SELECT state FROM payments.payment_anomalies WHERE id = '{id}'")).Should().Be("Open");

        var body = new { investigationConfirmed = true, resolution = "RefundedExternally", externalReference = "re_dash_1", note = "SECRET-NOTE-TEXT" };
        (await Resolve(body)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Resolve(body)).StatusCode.Should().Be(HttpStatusCode.OK, "an identical repeat is an idempotent replay");
        await AssertProblemAsync(await Resolve(new { investigationConfirmed = true, resolution = "NotOurPayment" }), HttpStatusCode.Conflict, "Operations.AnomalyResolutionConflict");

        (await StringAsync($"SELECT state || '|' || resolution || '|' || resolved_by::text FROM payments.payment_anomalies WHERE id = '{id}'")).Should().Be($"Resolved|RefundedExternally|{w.Admin.Id}");
        (await CountAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{id}' AND action = 'ResolvePaymentAnomaly'")).Should().Be(1);
        (await StringAsync($"SELECT detail FROM payments.admin_audit_log WHERE subject_id = '{id}'")).Should().NotContain("SECRET-NOTE-TEXT").And.Contain("NOT provider-verified");
        (await HistoryAsync($"entity_type = 'PaymentAnomaly' AND to_state = 'Resolved'")).Should().ContainSingle().Which
            .Should().Be("PaymentAnomaly|Open|Resolved|Transition|Administrator|AdministratorStatement|Stripe||anomaly_resolution_statement");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Initiated");
        (await CountAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be(0);
    }

    [Fact]
    public async Task PaymentAnomalies_AreProtectedByTheDatabase()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        await w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), $"cs_test_{attemptId[..8]}", 100, started.Currency, eventId: "evt_db"));
        var id = (await StringAsync("SELECT id FROM payments.payment_anomalies"))!;

        // An event that matched nothing cannot be linked to an order or attempt, and a link must be genuine.
        var falseLink = () => ExecAsync(
            "INSERT INTO payments.payment_anomalies (id, kind, reason_code, state, dedupe_key, provider, attempt_id, order_id, detected_at_utc) " +
            $"VALUES (gen_random_uuid(), 'UnknownPaymentReference', 'unknown_attempt', 'Open', 'k1', 'Stripe', '{attemptId}', '{order.OrderId}', now())");
        await falseLink.Should().ThrowAsync<PostgresException>().WithMessage("*chk_payment_anomalies_association*");
        var wrongProvider = () => ExecAsync(
            "INSERT INTO payments.payment_anomalies (id, kind, reason_code, state, dedupe_key, provider, attempt_id, order_id, expected_amount_minor, expected_currency, detected_at_utc) " +
            $"VALUES (gen_random_uuid(), 'AmountOrCurrencyMismatch', 'x', 'Open', 'k2', 'Paymob', '{attemptId}', '{order.OrderId}', 1, 'USD', now())");
        await wrongProvider.Should().ThrowAsync<PostgresException>().WithMessage("*own order and provider*");

        // Facts never change, a resolution is all or nothing, resolved is final, nothing is deleted.
        var facts = () => ExecAsync($"UPDATE payments.payment_anomalies SET received_amount_minor = 9100 WHERE id = '{id}'");
        await facts.Should().ThrowAsync<PostgresException>().WithMessage("*immutable*");
        var half = () => ExecAsync($"UPDATE payments.payment_anomalies SET state = 'Resolved' WHERE id = '{id}'");
        await half.Should().ThrowAsync<PostgresException>().WithMessage("*chk_payment_anomalies_resolution_fields*");
        await ExecAsync($"UPDATE payments.payment_anomalies SET state = 'Resolved', resolution = 'NoActionNeeded', resolved_by = '{w.Admin.Id}', resolved_at_utc = now() WHERE id = '{id}'");
        var reopen = () => ExecAsync($"UPDATE payments.payment_anomalies SET state = 'Open', resolution = NULL, resolved_by = NULL, resolved_at_utc = NULL WHERE id = '{id}'");
        await reopen.Should().ThrowAsync<PostgresException>().WithMessage("*final*");
        var delete = () => ExecAsync($"DELETE FROM payments.payment_anomalies WHERE id = '{id}'");
        await delete.Should().ThrowAsync<PostgresException>().WithMessage("*cannot be deleted*");
    }

    // ───────────── eligibility ─────────────

    [Fact]
    public async Task PaymentOptions_OfAnOrderWithAnException_OfferNothing_ForEveryProvider()
    {
        var (w, user, order, started, attemptId, session) = await FailedAttemptAsync();
        await LateSuccessAsync(w, attemptId, order, started, session);

        var options = await user.Client.GetFromJsonAsync<JsonElement>($"/api/orders/{order.OrderId}/payment-options");

        options.GetProperty("eligible").GetBoolean().Should().BeFalse();
        options.GetProperty("reason").GetString().Should().Be("OrderHeldForReview");
        options.GetProperty("providers").GetArrayLength().Should().Be(0);
        options.ToString().ToLowerInvariant().Should().NotContain("exception").And.NotContain("evt_");
    }
}
