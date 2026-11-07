using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using PulseReserve.AppHost.Api;
using PulseReserve.IntegrationTests.Catalog;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Reservation.Features.Holds;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

// Reconciliation, refunds, the outbox, tickets, notifications and the operations interface, against disposable
// PostgreSQL. The workers are driven explicitly (PaymentsWorkflow.RunOnceAsync) so every interleaving is deterministic;
// one test enables the hosted worker on a fresh host to prove recovery after a restart.
public class PaymentWorkflowHttpTests : IClassFixture<PostgreSqlDatabaseFixture>, IDisposable
{
    private readonly PostgreSqlDatabaseFixture _database;
    private readonly ScriptedPaymentProvider _provider = new();
    private readonly RecordingNotificationSender _sender = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly CatalogApiFactory _factory;
    private readonly TestUser _admin;

    public PaymentWorkflowHttpTests(PostgreSqlDatabaseFixture database)
    {
        _database = database;
        // The tests of this class share one container and assert global row counts, so each starts empty (before its admin registers).
        database.ResetAsync().GetAwaiter().GetResult();
        _factory = NewHost();
        _admin = AdminTestSupport.RegisterAdmin(_factory);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
    }

    private CatalogApiFactory NewHost(Dictionary<string, string?>? settings = null, Action<IServiceCollection>? more = null)
    {
        var host = new CatalogApiFactory(_database, settings, services =>
        {
            // The scripted provider REPLACES the simulator: with several providers registered a payment must name one.
            services.RemoveAll<PulseReserve.Modules.Payments.Features.IPaymentProvider>();
            services.AddSingleton<PulseReserve.Modules.Payments.Features.IPaymentProvider>(_provider);
            services.AddSingleton<INotificationSender>(_sender);
            more?.Invoke(services);
        });
        _disposables.Add(host);
        return host;
    }

    // ───────────── reconciliation ─────────────

    [Fact]
    public async Task Reconciliation_FinishesAPaymentThatCrashedBetweenTheVerdictAndTheSale_ExactlyOnce()
    {
        var (user, sale, started) = await StartedPaymentAsync(seats: 2);
        await CrashAfterVerdictAsync(started);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("PendingPayment");

        var first = await RunAsync();
        var second = await RunAsync();
        var third = await RunAsync();

        first.TasksDiscovered.Should().BeGreaterThan(0);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("Paid");
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{started.ReservationId}'")).Should().Be("Confirmed");
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Sold'")).Should().Be(2);
        (await StringAsync($"SELECT state FROM payments.reconciliation_tasks WHERE kind = 'FinalizeSuccessfulPayment' AND subject_id = '{started.Payment!.AttemptId}'")).Should().Be("Done");
        (await StringAsync($"SELECT last_outcome FROM payments.reconciliation_tasks WHERE kind = 'FinalizeSuccessfulPayment' AND subject_id = '{started.Payment.AttemptId}'")).Should().Be("Finalized as Paid");

        // Repeated runs change nothing: one task, two outbox events, two tickets, one notification.
        second.TasksProcessed.Should().Be(0);
        third.TasksProcessed.Should().Be(0);
        (await ScalarAsync($"SELECT count(*) FROM payments.reconciliation_tasks WHERE subject_id = '{started.Payment.AttemptId}'")).Should().Be(1);
        (await ScalarAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{started.OrderId}'")).Should().Be(2);
        (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(2);
        _sender.Sent.Count(n => n.OrderId == started.OrderId).Should().Be(1);
        var read = await user.Client.GetFromJsonAsync<OrderResponse>($"/api/orders/{started.OrderId}");
        read!.Status.Should().Be("Paid");
    }

    [Fact]
    public async Task Reconciliation_NeverSellsSeatsOfAnExpiredHold_ItRequestsAndPerformsARefund()
    {
        var (_, sale, started) = await StartedPaymentAsync();
        await CrashAfterVerdictAsync(started);
        await ExpireReservationAsync(started.ReservationId);

        await RunAsync();

        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("Refunded");
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Sold'")).Should().Be(0);
        (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(0);
        var refund = await StringAsync($"SELECT state || '|' || reason || '|' || is_simulated || '|' || amount FROM payments.refunds WHERE order_id = '{started.OrderId}'");
        refund.Should().Be("Succeeded|PaymentAfterSaleFailed|true|50.0000");
    }

    [Fact]
    public async Task Reconciliation_NeverSellsSeatsThatWereReassigned()
    {
        var (_, sale, started) = await StartedPaymentAsync();
        await CrashAfterVerdictAsync(started);
        await ExpireReservationAsync(started.ReservationId);
        var next = await AuthTestSupport.RegisterAsync(_factory);
        var nextHold = await HoldAsync(next, sale, 0);

        await RunAsync();

        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("Refunded");
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Held' AND reservation_id = '{nextHold.ReservationId}'")).Should().Be(1);
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{nextHold.ReservationId}'")).Should().Be("Held");
    }

    [Fact]
    public async Task ConcurrentReconciliationRuns_FinalizeEachOrderOnce_WithoutDuplicatesOrErrors()
    {
        var started = new List<(TestUser User, OnSaleEventBuilder.SaleSetup Sale, OrderResponse Order)>();
        for (var i = 0; i < 3; i++)
        {
            var (user, sale, order) = await StartedPaymentAsync(seats: 2);
            await CrashAfterVerdictAsync(order);
            started.Add((user, sale, order));
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RunAsync())));

        results.Sum(r => r.TasksFailed + r.RefundsFailed + r.OutboxFailed).Should().Be(0);
        foreach (var (_, sale, order) in started)
        {
            (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Paid");
            (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be(2);
            (await ScalarAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{order.OrderId}'")).Should().Be(2);
            (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Sold'")).Should().Be(2);
            _sender.Sent.Count(n => n.OrderId == order.OrderId).Should().Be(1);
        }
    }

    [Fact]
    public async Task Reconciliation_AfterAProcessRestart_ADifferentHostWithTheHostedWorkerRecoversThePendingWork()
    {
        var (_, _, started) = await StartedPaymentAsync();
        await CrashAfterVerdictAsync(started);

        // "Restart": a brand new host (no memory of anything) with the real hosted worker enabled.
        var restarted = NewHost(new Dictionary<string, string?>
        {
            ["Payments:Workers:Enabled"] = "true",
            ["Payments:Workers:IntervalSeconds"] = "1"
        });
        _ = restarted.CreateClient();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        string? status = null;
        while (DateTime.UtcNow < deadline)
        {
            status = await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'");
            if (status == "Paid" && await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{started.OrderId}'") == 1)
                break;
            await Task.Delay(500);
        }

        status.Should().Be("Paid");
        (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(1);
    }

    [Fact]
    public async Task ADuplicateSuccessCallback_AfterFinalisation_AddsNoSecondEventTicketOrRefund()
    {
        var (user, _, started) = await StartedPaymentAsync();
        (await CompleteAsync(user, started, "succeed")).StatusCode.Should().Be(HttpStatusCode.OK);
        await RunAsync();
        (await CompleteAsync(user, started, "succeed")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CompleteAsync(user, started, "succeed")).StatusCode.Should().Be(HttpStatusCode.OK);
        await RunAsync();

        (await ScalarAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{started.OrderId}'")).Should().Be(2);
        (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(1);
        (await ScalarAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{started.OrderId}'")).Should().Be(0);
    }

    [Fact]
    public async Task AmbiguousProviderAnswer_ForAStaleAttempt_IsNeverReadAsSuccessOrFailure()
    {
        using var stale = NewHost(new Dictionary<string, string?> { ["Payments:Workflow:StaleAttemptAfterSeconds"] = "0" });
        var (user, _, started) = await StartedPaymentAsync(stale);
        _provider.PaymentQueryThrows = true;

        var run = await RunAsync(stale);

        run.TasksFailed.Should().BeGreaterThan(0);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("PendingPayment");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{started.Payment!.AttemptId}'")).Should().Be("Initiated");
        var row = await StringAsync($"SELECT state || '|' || attempts || '|' || last_outcome FROM payments.reconciliation_tasks WHERE kind = 'ResolveStaleAttempt' AND subject_id = '{started.Payment.AttemptId}'");
        row.Should().Be("Pending|1|Provider status unknown");
        (await StringAsync($"SELECT last_error FROM payments.reconciliation_tasks WHERE kind = 'ResolveStaleAttempt' AND subject_id = '{started.Payment.AttemptId}'")).Should().Contain("TimeoutException");

        // Still unknown on the next pass: still nothing is inferred.
        await RunAsync(stale);
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{started.Payment.AttemptId}'")).Should().Be("Initiated");

        // The provider finally answers: only now is the verdict applied.
        _provider.PaymentQueryThrows = false;
        _provider.PaymentStatus = (_, _) => ProviderPaymentState.Succeeded;
        await RunAsync(stale);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("Paid");
        _provider.PaymentStatus = (_, reference) => reference is null ? ProviderPaymentState.NotFound : ProviderPaymentState.Pending;
        user.Client.Should().NotBeNull();
    }

    [Fact]
    public async Task StaleAttempt_ProviderSaysFailed_RecordsTheFailure_AndNotFoundWithoutReference_CancelsIt()
    {
        using var stale = NewHost(new Dictionary<string, string?> { ["Payments:Workflow:StaleAttemptAfterSeconds"] = "0" });
        var (_, _, failedOrder) = await StartedPaymentAsync(stale);
        var (_, _, neverSent) = await StartedPaymentAsync(stale);
        await ExecAsync($"UPDATE payments.payment_attempts SET provider_reference = NULL WHERE id = '{neverSent.Payment!.AttemptId}'");
        _provider.PaymentStatus = (attemptId, reference) =>
            attemptId == failedOrder.Payment!.AttemptId ? ProviderPaymentState.Failed
            : reference is null ? ProviderPaymentState.NotFound
            : ProviderPaymentState.Pending;

        await RunAsync(stale);

        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{failedOrder.Payment!.AttemptId}'")).Should().Be("Failed");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{failedOrder.OrderId}'")).Should().Be("PendingPayment");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{neverSent.Payment.AttemptId}'")).Should().Be("Cancelled");
        _provider.PaymentStatus = (_, reference) => reference is null ? ProviderPaymentState.NotFound : ProviderPaymentState.Pending;
    }

    [Fact]
    public async Task OverdueOrders_AreExpiredAndTheirReservationsClosed()
    {
        using var shortHold = NewHost(new Dictionary<string, string?> { ["Reservation:HoldDurationSeconds"] = "3" });
        var admin = await AdminTestSupport.RegisterAdminAsync(shortHold);
        var sale = await OnSaleEventBuilder.CreateAsync(admin.Client, seatCount: 1);
        var user = await AuthTestSupport.RegisterAsync(shortHold);
        var reservation = await HoldAsync(user, sale, 0);
        var order = await CheckoutAsync(user, reservation);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");

        await Task.Delay(3500);
        await RunAsync(shortHold);

        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Expired");
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{reservation.ReservationId}'")).Should().Be("Expired");
        (await ScalarAsync($"SELECT count(*) FROM reservation.event_seats WHERE event_id = '{sale.EventId}' AND status = 'Available'")).Should().Be(1);
        (await StringAsync($"SELECT state FROM payments.reconciliation_tasks WHERE kind = 'ExpireOrder' AND subject_id = '{order.OrderId}'")).Should().Be("Done");
    }

    [Fact]
    public async Task APoisonTask_DoesNotStopTheRestOfTheBatch_AndEndsInNeedsReview()
    {
        var poison = new ConcurrentDictionary<Guid, bool>();
        using var host = NewHost(new Dictionary<string, string?> { ["Payments:Workflow:MaxAttempts"] = "2" }, s => PoisonableGateway.Install(s, poison));
        var (_, _, bad) = await StartedPaymentAsync(host);
        var (_, _, good) = await StartedPaymentAsync(host);
        await CrashAfterVerdictAsync(bad);
        await CrashAfterVerdictAsync(good);
        poison[bad.ReservationId] = true;

        var first = await RunAsync(host);
        first.TasksFailed.Should().BeGreaterThanOrEqualTo(1);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{good.OrderId}'")).Should().Be("Paid");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{bad.OrderId}'")).Should().Be("PendingPayment");
        (await StringAsync($"SELECT state FROM payments.reconciliation_tasks WHERE subject_id = '{bad.Payment!.AttemptId}'")).Should().Be("Pending");

        await RunAsync(host); // second (= final) attempt
        (await StringAsync($"SELECT state FROM payments.reconciliation_tasks WHERE subject_id = '{bad.Payment.AttemptId}'")).Should().Be("NeedsReview");
        (await StringAsync($"SELECT last_error FROM payments.reconciliation_tasks WHERE subject_id = '{bad.Payment.AttemptId}'")).Should().Contain("InvalidOperationException").And.NotContain("scripted_");

        // Manual intervention: the cause is fixed, an administrator retries, the task completes.
        poison.Clear();
        var taskId = await StringAsync($"SELECT id FROM payments.reconciliation_tasks WHERE subject_id = '{bad.Payment.AttemptId}'");
        var admin = await AdminTestSupport.RegisterAdminAsync(host);
        (await admin.Client.PostAsync($"/api/admin/payments/reconciliation-tasks/{taskId}/retry", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await RunAsync(host);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{bad.OrderId}'")).Should().Be("Paid");
        (await StringAsync($"SELECT action || '|' || actor_user_id FROM payments.admin_audit_log WHERE subject_id = '{taskId}'")).Should().Be($"RetryReconciliationTask|{admin.Id}");
    }

    // ───────────── refunds ─────────────

    [Fact]
    public async Task Refund_ProviderConfirmation_IsRequiredBeforeAnythingIsClaimed()
    {
        _provider.Mode = RefundMode.Pending;
        var order = await RefundRequiredOrderAsync();

        await RunAsync();
        (await StringAsync($"SELECT state FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be("Pending");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("RefundRequired", "not refunded until the provider confirms");

        await RunAsync(); // still pending at the provider
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("RefundRequired");

        _provider.Mode = RefundMode.Succeed;
        await RunAsync();
        (await StringAsync($"SELECT state FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be("Succeeded");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Refunded");
    }

    [Fact]
    public async Task Refund_PermanentFailure_StaysVisible_NeverChangesTheOrderSilently_AndAdminRetryUsesANewSubmission()
    {
        _provider.Mode = RefundMode.RejectPermanent;
        var order = await RefundRequiredOrderAsync();
        await RunAsync();

        var refundId = Guid.Parse((await StringAsync($"SELECT id FROM payments.refunds WHERE order_id = '{order.OrderId}'"))!);
        (await StringAsync($"SELECT state || '|' || last_error FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Failed|account_closed");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("RefundRequired");

        await RunAsync(); // a Failed refund is terminal for the worker: nothing happens by itself
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Failed");

        _provider.Mode = RefundMode.Succeed;
        var response = await _admin.Client.PostAsync($"/api/admin/payments/refunds/{refundId}/retry", null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StringAsync($"SELECT state || '|' || submission_number FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Requested|2");
        await RunAsync();

        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Succeeded");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Refunded");
        (await StringAsync($"SELECT action || '|' || actor_user_id || '|' || detail FROM payments.admin_audit_log WHERE subject_id = '{refundId}'"))
            .Should().Be($"RetryRefund|{_admin.Id}|Failed -> Requested");
    }

    [Fact]
    public async Task Refund_RetryableRejection_IsResubmittedWithANewIdempotencyKey()
    {
        _provider.Mode = RefundMode.RejectRetryable;
        var order = await RefundRequiredOrderAsync();
        await RunAsync();
        (await StringAsync($"SELECT state || '|' || submission_number FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be("Requested|2");

        _provider.Mode = RefundMode.Succeed;
        await RunAsync();
        (await StringAsync($"SELECT state FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be("Succeeded");
    }

    [Fact]
    public async Task Refund_AmbiguousAnswerAfterTheProviderProcessedIt_IsResolvedByQuerying_NotByRefundingAgain()
    {
        _provider.Mode = RefundMode.TimeoutAfterProcessing;
        var order = await RefundRequiredOrderAsync();
        await RunAsync();

        var (refundId, key) = await RefundIdentityAsync(order.OrderId);
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Unknown");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("RefundRequired");
        _provider.MoneyMoved(refundId).Should().Be(1);

        await RunAsync(); // provider still reachable: query finds the refund it already made
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Succeeded");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Refunded");
        _provider.RefundCalls(key).Should().Be(1, "the refund request was NOT sent a second time");
        _provider.MoneyMoved(refundId).Should().Be(1);
    }

    [Fact]
    public async Task Refund_AmbiguousAnswerWhileTheProviderIsUnreachable_NeverSubmitsBlindly()
    {
        _provider.Mode = RefundMode.TimeoutAfterProcessing;
        var order = await RefundRequiredOrderAsync();
        await RunAsync();
        var (refundId, key) = await RefundIdentityAsync(order.OrderId);

        _provider.QueryRefundThrows = true; // cannot even ask
        for (var i = 0; i < 3; i++)
            await RunAsync();

        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Unknown");
        _provider.RefundCalls(key).Should().Be(1);
        _provider.MoneyMoved(refundId).Should().Be(1);

        _provider.QueryRefundThrows = false;
        await RunAsync();
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Succeeded");
        _provider.RefundCalls(key).Should().Be(1);
    }

    [Fact]
    public async Task Refund_AmbiguousAnswerWhenTheRequestNeverArrived_IsSubmittedAfterTheProviderConfirmsItKnowsNothing()
    {
        _provider.Mode = RefundMode.TimeoutBeforeProcessing;
        var order = await RefundRequiredOrderAsync();
        await RunAsync();
        var (refundId, key) = await RefundIdentityAsync(order.OrderId);
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Unknown");
        _provider.MoneyMoved(refundId).Should().Be(0);

        _provider.Mode = RefundMode.Succeed;
        await RunAsync(); // query says NotFound -> safe to submit (same key)

        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Succeeded");
        _provider.MoneyMoved(refundId).Should().Be(1);
        _provider.RefundCalls(key).Should().Be(2);
    }

    [Fact]
    public async Task Refund_ExhaustedAttempts_NeedReview_AndAnAdminRetryRequeriesBeforeAnySubmission()
    {
        using var host = NewHost(new Dictionary<string, string?> { ["Payments:Workflow:MaxAttempts"] = "2" });
        _provider.Mode = RefundMode.TimeoutAfterProcessing;
        var order = await RefundRequiredOrderAsync(host);
        await RunAsync(host);
        var (refundId, key) = await RefundIdentityAsync(order.OrderId);
        _provider.QueryRefundThrows = true;
        await RunAsync(host);
        await RunAsync(host);

        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("NeedsReview");
        (await ScalarAsync("SELECT count(*) FROM payments.refunds WHERE state = 'NeedsReview'")).Should().BeGreaterThanOrEqualTo(1);

        _provider.QueryRefundThrows = false;
        var admin = await AdminTestSupport.RegisterAdminAsync(host);
        (await admin.Client.PostAsync($"/api/admin/payments/refunds/{refundId}/retry", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StringAsync($"SELECT state || '|' || submission_number FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Unknown|1");
        await RunAsync(host);

        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Succeeded");
        _provider.RefundCalls(key).Should().Be(1);
        _provider.MoneyMoved(refundId).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentWorkers_RefundExactlyOnce()
    {
        _provider.Mode = RefundMode.Succeed;
        var order = await RefundRequiredOrderAsync();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RunAsync())));

        var (refundId, key) = await RefundIdentityAsync(order.OrderId);
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Succeeded");
        _provider.RefundCalls(key).Should().Be(1);
        _provider.MoneyMoved(refundId).Should().Be(1);
        (await ScalarAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be(1);
    }

    [Fact]
    public async Task AdminRefundRequest_IsAuditedIdempotentAndOnlyForOrdersThatRequireARefund()
    {
        _provider.Mode = RefundMode.Pending; // keep it visible
        var order = await RefundRequiredOrderAsync();
        // Simulate "no refund record yet" (e.g. created before the workflow existed).
        await ExecAsync("ALTER TABLE payments.refunds DISABLE TRIGGER trg_refunds_guard_update");
        await ExecAsync($"DELETE FROM payments.refunds WHERE order_id = '{order.OrderId}'");
        await ExecAsync("ALTER TABLE payments.refunds ENABLE TRIGGER trg_refunds_guard_update");

        var first = await _admin.Client.PostAsync($"/api/admin/payments/orders/{order.OrderId}/refund", null);
        var second = await _admin.Client.PostAsync($"/api/admin/payments/orders/{order.OrderId}/refund", null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var a = await first.Content.ReadFromJsonAsync<JsonElement>();
        var b = await second.Content.ReadFromJsonAsync<JsonElement>();
        a.GetProperty("refundId").GetGuid().Should().Be(b.GetProperty("refundId").GetGuid());
        a.GetProperty("requestedBy").GetGuid().Should().Be(_admin.Id);
        a.GetProperty("reason").GetString().Should().Be("AdminRequested");
        (await ScalarAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{order.OrderId}' AND action = 'RequestRefund'")).Should().Be(1);

        // A paid order cannot be refunded through this path; unknown orders are 404.
        var (_, _, paidStart) = await StartedPaymentAsync();
        var paidUser = await UserOfAsync(paidStart);
        (await CompleteAsync(paidUser, paidStart, "succeed")).StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertProblemAsync(await _admin.Client.PostAsync($"/api/admin/payments/orders/{paidStart.OrderId}/refund", null), HttpStatusCode.Conflict, "Operations.InvalidState");
        await AssertProblemAsync(await _admin.Client.PostAsync($"/api/admin/payments/orders/{Guid.NewGuid()}/refund", null), HttpStatusCode.NotFound, "Operations.NotFound");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{paidStart.OrderId}'")).Should().Be("Paid");
    }

    [Fact]
    public async Task RefundRecords_AreProtectedByTheDatabase()
    {
        var order = await RefundRequiredOrderAsync();
        var (refundId, _) = await RefundIdentityAsync(order.OrderId);

        var change = () => ExecAsync($"UPDATE payments.refunds SET amount = 1 WHERE id = '{refundId}'");
        await change.Should().ThrowAsync<PostgresException>().WithMessage("*immutable*");
        var delete = () => ExecAsync($"DELETE FROM payments.refunds WHERE id = '{refundId}'");
        await delete.Should().ThrowAsync<PostgresException>();
        var duplicate = () => ExecAsync(
            $"INSERT INTO payments.refunds (id, order_id, payment_attempt_id, amount, currency, reason, state, provider, is_simulated, submission_number, attempts, next_attempt_at_utc, created_at_utc, updated_at_utc) " +
            $"SELECT gen_random_uuid(), order_id, payment_attempt_id, amount, currency, reason, 'Requested', provider, is_simulated, 1, 0, now(), now(), now() FROM payments.refunds WHERE id = '{refundId}'");
        await duplicate.Should().ThrowAsync<PostgresException>("one refund per order");

        // A refund for an order that is not RefundRequired (here: Pending) cannot even be inserted.
        var sale = await OnSaleEventBuilder.CreateAsync(_admin.Client, seatCount: 1);
        var user = await AuthTestSupport.RegisterAsync(_factory);
        var pending = await CheckoutAsync(user, await HoldAsync(user, sale, 0));
        var bogus = () => ExecAsync(
            $"INSERT INTO payments.refunds (id, order_id, payment_attempt_id, amount, currency, reason, state, provider, is_simulated, submission_number, attempts, next_attempt_at_utc, created_at_utc, updated_at_utc) " +
            $"SELECT gen_random_uuid(), '{pending.OrderId}', payment_attempt_id, 1, 'USD', 'X', 'Requested', provider, is_simulated, 1, 0, now(), now(), now() FROM payments.refunds WHERE id = '{refundId}'");
        await bogus.Should().ThrowAsync<PostgresException>();

        // A paid order can never leave Paid, and RefundRequired can only become Refunded.
        var (_, _, paid) = await StartedPaymentAsync();
        await CompleteAsync(await UserOfAsync(paid), paid, "succeed");
        var unpay = () => ExecAsync($"UPDATE payments.orders SET status = 'Cancelled' WHERE id = '{paid.OrderId}'");
        await unpay.Should().ThrowAsync<PostgresException>().WithMessage("*final*");
    }

    // ───────────── outbox, tickets, notifications ─────────────

    [Fact]
    public async Task PaidOrder_AlwaysHasItsOutboxEvents_CommittedWithThePaidState()
    {
        var (user, _, started) = await StartedPaymentAsync(seats: 3);
        (await CompleteAsync(user, started, "succeed")).StatusCode.Should().Be(HttpStatusCode.OK);

        // No worker has run yet: the events are already durable, written in the same transaction as "Paid".
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("Paid");
        (await StringAsync($"SELECT string_agg(type || ':' || state, ',' ORDER BY type) FROM payments.outbox_events WHERE aggregate_id = '{started.OrderId}'"))
            .Should().Be("IssueTickets:Pending,NotifyOrderPaid:Pending");
        (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(0);

        await RunAsync();

        (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(3);
        (await ScalarAsync($"SELECT count(DISTINCT public_id) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(3);
        (await ScalarAsync($"SELECT min(length(public_id)) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().BeGreaterThanOrEqualTo(40);
        (await StringAsync($"SELECT string_agg(state, ',') FROM payments.outbox_events WHERE aggregate_id = '{started.OrderId}'")).Should().Be("Done,Done");
        _sender.Sent.Where(n => n.OrderId == started.OrderId).Should().ContainSingle().Which.TicketCount.Should().Be(3);
    }

    [Fact]
    public async Task RepeatedOutboxDelivery_NeverDuplicatesTicketsOrNotifications()
    {
        var (user, _, started) = await StartedPaymentAsync(seats: 2);
        await CompleteAsync(user, started, "succeed");
        await RunAsync();
        var idsBefore = await StringAsync($"SELECT string_agg(public_id, ',' ORDER BY seat_id) FROM payments.tickets WHERE order_id = '{started.OrderId}'");

        for (var i = 0; i < 3; i++)
        {
            await ExecAsync($"UPDATE payments.outbox_events SET state = 'Pending', attempts = 0, next_attempt_at_utc = now() WHERE aggregate_id = '{started.OrderId}'");
            await RunAsync();
        }

        (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(2);
        (await StringAsync($"SELECT string_agg(public_id, ',' ORDER BY seat_id) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(idsBefore, "redelivery keeps the original tickets");
        _sender.Sent.Count(n => n.OrderId == started.OrderId).Should().Be(1);
        (await ScalarAsync($"SELECT count(*) FROM payments.notification_deliveries WHERE order_id = '{started.OrderId}'")).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentDelivery_IssuesExactlyOneTicketPerSeat()
    {
        var (user, _, started) = await StartedPaymentAsync(seats: 4);
        await CompleteAsync(user, started, "succeed");

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => RunAsync())));

        (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(4);
        (await ScalarAsync($"SELECT count(DISTINCT seat_id) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(4);
    }

    [Fact]
    public async Task NotificationFailure_NeverUndoesThePurchase_NorBlocksTickets_AndIsRetried()
    {
        _sender.Fail = true;
        var (user, _, started) = await StartedPaymentAsync(seats: 2);
        await CompleteAsync(user, started, "succeed");

        var run = await RunAsync();

        run.OutboxFailed.Should().BeGreaterThanOrEqualTo(1);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("Paid");
        (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(2);
        (await StringAsync($"SELECT state || '|' || attempts FROM payments.outbox_events WHERE aggregate_id = '{started.OrderId}' AND type = 'NotifyOrderPaid'")).Should().Be("Pending|1");
        (await StringAsync($"SELECT last_error FROM payments.outbox_events WHERE aggregate_id = '{started.OrderId}' AND type = 'NotifyOrderPaid'")).Should().Contain("InvalidOperationException");
        (await ScalarAsync($"SELECT count(*) FROM payments.notification_deliveries WHERE order_id = '{started.OrderId}'")).Should().Be(0);
        (await user.Client.GetFromJsonAsync<OrderResponse>($"/api/orders/{started.OrderId}"))!.Status.Should().Be("Paid");

        _sender.Fail = false;
        await RunAsync();
        _sender.Sent.Count(n => n.OrderId == started.OrderId).Should().Be(1);
        (await StringAsync($"SELECT state FROM payments.outbox_events WHERE aggregate_id = '{started.OrderId}' AND type = 'NotifyOrderPaid'")).Should().Be("Done");
    }

    [Fact]
    public async Task NoTickets_ForUnpaidCancelledOrRefundPendingOrders()
    {
        var sale = await OnSaleEventBuilder.CreateAsync(_admin.Client, seatCount: 3);
        var users = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => AuthTestSupport.RegisterAsync(_factory)));
        var pending = await CheckoutAsync(users[0], await HoldAsync(users[0], sale, 0));
        var cancelled = await CheckoutAsync(users[1], await HoldAsync(users[1], sale, 1));
        (await users[1].Client.PostAsync($"/api/orders/{cancelled.OrderId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        _provider.Mode = RefundMode.Pending;
        var refundRequired = await RefundRequiredOrderAsync();

        // Even a stray/forged IssueTickets event cannot produce tickets for these orders.
        foreach (var orderId in new[] { pending.OrderId, cancelled.OrderId, refundRequired.OrderId })
            await ExecAsync($"INSERT INTO payments.outbox_events (id, type, aggregate_id, state, attempts, next_attempt_at_utc, created_at_utc, updated_at_utc) VALUES (gen_random_uuid(), 'IssueTickets', '{orderId}', 'Pending', 0, now(), now(), now())");
        await RunAsync();

        foreach (var orderId in new[] { pending.OrderId, cancelled.OrderId, refundRequired.OrderId })
        {
            (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{orderId}'")).Should().Be(0);
            (await StringAsync($"SELECT state FROM payments.outbox_events WHERE aggregate_id = '{orderId}' AND type = 'IssueTickets'")).Should().Be("Pending", "the anomaly stays visible instead of being swallowed");
        }

        // And the database itself refuses.
        var direct = () => ExecAsync(
            $"INSERT INTO payments.tickets (id, public_id, order_id, seat_id, event_id, customer_id, status, issued_at_utc) " +
            $"SELECT gen_random_uuid(), 'x' || gen_random_uuid(), o.id, i.seat_id, o.event_id, o.customer_id, 'Valid', now() FROM payments.orders o JOIN payments.order_items i ON i.order_id = o.id WHERE o.id = '{pending.OrderId}'");
        await direct.Should().ThrowAsync<PostgresException>().WithMessage("*Paid*");
    }

    [Fact]
    public async Task Tickets_AreUniquePerSeat_Immutable_AndOnlyVisibleToTheirOwner()
    {
        var (user, sale, started) = await StartedPaymentAsync(seats: 2);
        await CompleteAsync(user, started, "succeed");
        await RunAsync();
        var other = await AuthTestSupport.RegisterAsync(_factory);

        var duplicate = () => ExecAsync(
            $"INSERT INTO payments.tickets (id, public_id, order_id, seat_id, event_id, customer_id, status, issued_at_utc) " +
            $"SELECT gen_random_uuid(), 'dup' || gen_random_uuid(), order_id, seat_id, event_id, customer_id, 'Valid', now() FROM payments.tickets WHERE order_id = '{started.OrderId}' LIMIT 1");
        await duplicate.Should().ThrowAsync<PostgresException>("one ticket per (order, seat)");
        var change = () => ExecAsync($"UPDATE payments.tickets SET status = 'Valid' WHERE order_id = '{started.OrderId}'");
        await change.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        var remove = () => ExecAsync($"DELETE FROM payments.tickets WHERE order_id = '{started.OrderId}'");
        await remove.Should().ThrowAsync<PostgresException>();

        var response = await user.Client.GetAsync($"/api/orders/{started.OrderId}/tickets");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tickets = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        tickets.Should().HaveCount(2);
        tickets.Select(t => t.GetProperty("seatId").GetGuid()).Should().BeEquivalentTo([sale.SeatIds[0], sale.SeatIds[1]]);
        tickets.Should().OnlyContain(t => t.GetProperty("ticketId").GetString()!.Length >= 40 && t.GetProperty("status").GetString() == "Valid");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(user.Email).And.NotContain(user.Id.ToString(), "no private customer data in ticket responses");

        await AssertProblemAsync(await other.Client.GetAsync($"/api/orders/{started.OrderId}/tickets"), HttpStatusCode.NotFound, "Order.NotFound");
        (await _factory.CreateClient().GetAsync($"/api/orders/{started.OrderId}/tickets")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task APaidOrderWhoseEventsAreMissing_IsRepairedByReconciliation()
    {
        var (user, _, started) = await StartedPaymentAsync();
        await CompleteAsync(user, started, "succeed");
        await ExecAsync($"DELETE FROM payments.outbox_events WHERE aggregate_id = '{started.OrderId}'");

        await RunAsync();

        (await ScalarAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{started.OrderId}'")).Should().Be(1);
        _sender.Sent.Count(n => n.OrderId == started.OrderId).Should().Be(1);
    }

    // ───────────── operations interface ─────────────

    public static TheoryData<string, string> OperationRoutes => new()
    {
        { "GET", "/api/admin/payments/overview" },
        { "GET", "/api/admin/payments/providers" },
        { "GET", "/api/admin/payments/reconciliation-tasks" },
        { "GET", "/api/admin/payments/refunds" },
        { "GET", "/api/admin/payments/outbox-events" },
        { "GET", "/api/admin/payments/audit-log" },
        { "GET", "/api/admin/payments/orders" },
        { "GET", "/api/admin/payments/orders/00000000-0000-4000-8000-000000000001" },
        { "POST", "/api/admin/payments/process-now" },
        { "POST", "/api/admin/payments/reconciliation-tasks/00000000-0000-4000-8000-000000000001/retry" },
        { "POST", "/api/admin/payments/refunds/00000000-0000-4000-8000-000000000001/retry" },
        { "POST", "/api/admin/payments/outbox-events/00000000-0000-4000-8000-000000000001/retry" },
        { "POST", "/api/admin/payments/orders/00000000-0000-4000-8000-000000000001/refund" }
    };

    [Theory]
    [MemberData(nameof(OperationRoutes))]
    public async Task OperationsRoutes_RejectAnonymousUsers_AndCustomers(string method, string path)
    {
        var anonymous = _factory.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(anonymous);
        await AssertProblemAsync(await Send(anonymous, method, path), HttpStatusCode.Unauthorized, "Auth.Unauthorized");

        var customer = await AuthTestSupport.RegisterAsync(_factory);
        await AssertProblemAsync(await Send(customer.Client, method, path), HttpStatusCode.Forbidden, "Auth.Forbidden");
    }

    [Fact]
    public async Task OperationsRoutes_NeedACsrfToken_ForAdminMutations_AndShowTheWorkToAdmins()
    {
        var admin = await AdminTestSupport.RegisterAdminAsync(_factory);
        admin.Client.DefaultRequestHeaders.Remove(AuthTestSupport.CsrfHeader);
        await AssertProblemAsync(await admin.Client.PostAsync("/api/admin/payments/process-now", null), HttpStatusCode.Forbidden, "Csrf.Invalid");
        admin.Client.DefaultRequestHeaders.Add(AuthTestSupport.CsrfHeader, "forged");
        await AssertProblemAsync(await admin.Client.PostAsync("/api/admin/payments/process-now", null), HttpStatusCode.Forbidden, "Csrf.Invalid");

        var overview = await _admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/overview");
        overview.TryGetProperty("reconciliationNeedsReview", out _).Should().BeTrue();
        (await _admin.Client.PostAsync("/api/admin/payments/process-now", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.Client.GetAsync("/api/admin/payments/refunds?state=Bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.Client.GetAsync("/api/admin/payments/refunds?state=Failed&limit=5")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RetryOperations_ValidateTheCurrentState_OnTheServer()
    {
        var (user, _, started) = await StartedPaymentAsync();
        await CompleteAsync(user, started, "succeed");
        await RunAsync();
        // A finished reconciliation task needs a payment that crashed between the verdict and the sale and was finished by the worker.
        var (_, _, crashed) = await StartedPaymentAsync();
        await CrashAfterVerdictAsync(crashed);
        await RunAsync();
        var doneTask = await StringAsync("SELECT id FROM payments.reconciliation_tasks WHERE state = 'Done' LIMIT 1");
        doneTask.Should().NotBeNull("the crashed payment was reconciled, which leaves a finished task");
        var doneEvent = await StringAsync($"SELECT id FROM payments.outbox_events WHERE aggregate_id = '{started.OrderId}' LIMIT 1");

        await AssertProblemAsync(await _admin.Client.PostAsync($"/api/admin/payments/outbox-events/{doneEvent}/retry", null), HttpStatusCode.Conflict, "Operations.InvalidState");
        await AssertProblemAsync(await _admin.Client.PostAsync($"/api/admin/payments/outbox-events/{Guid.NewGuid()}/retry", null), HttpStatusCode.NotFound, "Operations.NotFound");
        await AssertProblemAsync(await _admin.Client.PostAsync($"/api/admin/payments/reconciliation-tasks/{doneTask}/retry", null), HttpStatusCode.Conflict, "Operations.InvalidState");
        (await ScalarAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{doneEvent}'")).Should().Be(0, "rejected operations leave no audit entry");
    }

    [Fact]
    public async Task Overview_ReportsRealPerStateCounts_ThatMatchTheDatabase()
    {
        _provider.Mode = RefundMode.Pending;
        var order = await RefundRequiredOrderAsync();
        await RunAsync(); // refund becomes Pending, the paid-order events are delivered
        var (paidUser, _, paid) = await StartedPaymentAsync();
        await CompleteAsync(paidUser, paid, "succeed");

        var overview = await _admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/overview");

        foreach (var (table, property) in new[] { ("refunds", "refundsByState"), ("outbox_events", "outboxByState"), ("reconciliation_tasks", "tasksByState") })
        {
            var byState = overview.GetProperty(property).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
            foreach (var (state, count) in byState)
                count.Should().Be((int)await ScalarAsync($"SELECT count(*) FROM payments.{table} WHERE state = '{state}'"), $"{table}/{state}");
            byState.Values.Sum().Should().Be((int)await ScalarAsync($"SELECT count(*) FROM payments.{table}"));
        }
        overview.GetProperty("refundsByState").GetProperty("Pending").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        overview.GetProperty("outboxByState").GetProperty("Pending").GetInt32().Should().BeGreaterThanOrEqualTo(2);
        order.OrderId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task OperationsLists_ExposeTimingFields_AndOrdersShowRefundProgressWithoutCustomerData()
    {
        _provider.Mode = RefundMode.Pending;
        var order = await RefundRequiredOrderAsync();
        await RunAsync();

        var refunds = await _admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/refunds?state=Pending&limit=100");
        var refund = refunds.EnumerateArray().Single(r => r.GetProperty("orderId").GetGuid() == order.OrderId);
        refund.GetProperty("nextAttemptAtUtc").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddYears(-1));
        refund.GetProperty("updatedAtUtc").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddYears(-1));
        refund.GetProperty("isSimulated").GetBoolean().Should().BeTrue();

        // Outbox events exist only for a paid order: pay one so the list under test is not empty.
        var (paidUser, _, paid) = await StartedPaymentAsync();
        await CompleteAsync(paidUser, paid, "succeed");

        var events = await _admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/outbox-events?limit=200");
        events.EnumerateArray().Should().NotBeEmpty().And.OnlyContain(e => e.GetProperty("nextAttemptAtUtc").ValueKind == JsonValueKind.String && e.GetProperty("updatedAtUtc").ValueKind == JsonValueKind.String);

        var orders = await _admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/orders?status=RefundRequired&limit=200");
        var row = orders.EnumerateArray().Single(o => o.GetProperty("orderId").GetGuid() == order.OrderId);
        row.GetProperty("status").GetString().Should().Be("RefundRequired");
        row.GetProperty("totalAmount").GetDecimal().Should().Be(50m);
        row.GetProperty("refundState").GetString().Should().Be("Pending");
        row.GetProperty("paymentIsSimulated").GetBoolean().Should().BeTrue();
        var json = row.GetRawText();
        json.Should().NotContainAny("customer", "Customer", "token", "reference");

        var single = await _admin.Client.GetFromJsonAsync<JsonElement>($"/api/admin/payments/orders/{order.OrderId}");
        single.GetProperty("refundId").GetGuid().Should().Be(row.GetProperty("refundId").GetGuid());
        await AssertProblemAsync(await _admin.Client.GetAsync($"/api/admin/payments/orders/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "Order.NotFound");
        (await _admin.Client.GetAsync("/api/admin/payments/orders?status=Bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ───────────── manual refund completion (needs PostgreSQL) ─────────────

    private async Task<(OrderResponse Order, Guid RefundId)> NeedsReviewManualRefundAsync()
    {
        _provider.SupportsRefunds = false;
        var order = await RefundRequiredOrderAsync();
        await RunAsync();
        var (refundId, _) = await RefundIdentityAsync(order.OrderId);
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("NeedsReview");
        return (order, refundId);
    }

    private async Task<object> ManualBodyAsync(Guid refundId, string? reference = "REF-1", bool confirmed = true, decimal? amount = null, string? currency = null) => new
    {
        confirmedPerformedExternally = confirmed,
        amount = amount ?? decimal.Parse((await StringAsync($"SELECT amount::text FROM payments.refunds WHERE id = '{refundId}'"))!, System.Globalization.CultureInfo.InvariantCulture),
        currency = currency ?? await StringAsync($"SELECT currency FROM payments.refunds WHERE id = '{refundId}'"),
        externalReference = reference,
        note = "SECRET-NOTE-TEXT"
    };

    private Task<HttpResponseMessage> RecordManualAsync(Guid refundId, object body) =>
        _admin.Client.PostAsJsonAsync($"/api/admin/payments/refunds/{refundId}/manual-completion", body);

    [Fact]
    public async Task ManualRefund_IsRecordedAtomically_AsManuallyRecorded_NotProviderConfirmed_AndAudited()
    {
        var (order, refundId) = await NeedsReviewManualRefundAsync();

        var response = await RecordManualAsync(refundId, await ManualBodyAsync(refundId));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("state").GetString().Should().Be("ManuallyRecorded");
        json.GetProperty("manuallyRecordedBy").GetGuid().Should().Be(_admin.Id);
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("ManuallyRecorded");
        (await StringAsync($"SELECT COALESCE(provider_refund_id, 'none') FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("none", "no provider confirmed anything");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Refunded");
        (await ScalarAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{refundId}' AND action = 'RecordManualRefund' AND actor_user_id = '{_admin.Id}'")).Should().Be(1);
        (await StringAsync($"SELECT detail FROM payments.admin_audit_log WHERE subject_id = '{refundId}' AND action = 'RecordManualRefund'")).Should().NotContain("SECRET-NOTE-TEXT").And.Contain("NOT provider-confirmed");
        _provider.MoneyMoved(refundId).Should().Be(0, "no provider API is called for a manual record");
    }

    [Fact]
    public async Task ManualRefund_AmountOrCurrencyMismatch_OrMissingConfirmation_ChangesNothing()
    {
        var (order, refundId) = await NeedsReviewManualRefundAsync();

        await AssertProblemAsync(await RecordManualAsync(refundId, await ManualBodyAsync(refundId, amount: 1m)), HttpStatusCode.Conflict, "Operations.ManualRefundMismatch");
        await AssertProblemAsync(await RecordManualAsync(refundId, await ManualBodyAsync(refundId, currency: "EUR")), HttpStatusCode.Conflict, "Operations.ManualRefundMismatch");
        await AssertProblemAsync(await RecordManualAsync(refundId, await ManualBodyAsync(refundId, confirmed: false)), HttpStatusCode.BadRequest, "Operations.ManualRefundNotConfirmed");

        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("NeedsReview");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("RefundRequired");
        (await ScalarAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{refundId}' AND action = 'RecordManualRefund'")).Should().Be(0);
    }

    [Fact]
    public async Task ManualRefund_IsRefusedForAProviderThatRefundsThroughTheApi_AndForRefundsThatAreNotWaitingForReview()
    {
        // A provider that CAN refund: a failed refund keeps its automatic retry path; it cannot be recorded by hand.
        _provider.Mode = RefundMode.RejectPermanent;
        var automatic = await RefundRequiredOrderAsync();
        await RunAsync();
        var (automaticId, _) = await RefundIdentityAsync(automatic.OrderId);
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{automaticId}'")).Should().Be("Failed");
        await AssertProblemAsync(await RecordManualAsync(automaticId, await ManualBodyAsync(automaticId)), HttpStatusCode.Conflict, "Operations.ManualRefundNotAllowed");

        // A provider that cannot refund, but the worker has not escalated the refund yet (still Requested).
        _provider.SupportsRefunds = false;
        _provider.Mode = RefundMode.Succeed;
        var early = await RefundRequiredOrderAsync();
        var (earlyId, _) = await RefundIdentityAsync(early.OrderId);
        await AssertProblemAsync(await RecordManualAsync(earlyId, await ManualBodyAsync(earlyId)), HttpStatusCode.Conflict, "Operations.ManualRefundNotAllowed");

        await AssertProblemAsync(await RecordManualAsync(Guid.NewGuid(), new { confirmedPerformedExternally = true, amount = 1m, currency = "USD" }), HttpStatusCode.NotFound, "Operations.NotFound");
        (await ScalarAsync("SELECT count(*) FROM payments.refunds WHERE state = 'ManuallyRecorded'")).Should().Be(0);
    }

    [Fact]
    public async Task ManualRefund_RepeatedIdentically_IsIdempotent_ButADifferentRecordIsAConflict_AndNothingOverwrites()
    {
        var (_, refundId) = await NeedsReviewManualRefundAsync();
        var body = await ManualBodyAsync(refundId);

        (await RecordManualAsync(refundId, body)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RecordManualAsync(refundId, body)).StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertProblemAsync(await RecordManualAsync(refundId, await ManualBodyAsync(refundId, reference: "OTHER")), HttpStatusCode.Conflict, "Operations.ManualRefundConflict");

        (await StringAsync($"SELECT manual_reference FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("REF-1");
        (await ScalarAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{refundId}' AND action = 'RecordManualRefund'")).Should().Be(1, "a replay audits nothing new");
    }

    [Fact]
    public async Task ManualRefund_ConcurrentSubmissions_RecordExactlyOnce()
    {
        var (_, refundId) = await NeedsReviewManualRefundAsync();
        var bodies = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ManualBodyAsync(refundId, reference: $"REF-{i % 2}")));

        var responses = await Task.WhenAll(bodies.Select(b => Task.Run(() => RecordManualAsync(refundId, b))));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().BeGreaterThanOrEqualTo(1);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        (await ScalarAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{refundId}' AND action = 'RecordManualRefund'")).Should().Be(1);
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("ManuallyRecorded");
    }

    [Fact]
    public async Task ManualRefund_TheWorkerNeverTouchesIt_AndTheDatabaseKeepsItFinal()
    {
        var (_, refundId) = await NeedsReviewManualRefundAsync();
        (await RecordManualAsync(refundId, await ManualBodyAsync(refundId))).StatusCode.Should().Be(HttpStatusCode.OK);

        await RunAsync();
        _provider.Mode = RefundMode.Succeed;
        await RunAsync();

        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("ManuallyRecorded");
        _provider.MoneyMoved(refundId).Should().Be(0);
        var reopen = () => ExecAsync($"UPDATE payments.refunds SET state = 'Requested' WHERE id = '{refundId}'");
        await reopen.Should().ThrowAsync<PostgresException>().WithMessage("*final*");
        var rewrite = () => ExecAsync($"UPDATE payments.refunds SET manual_reference = 'CHANGED' WHERE id = '{refundId}'");
        await rewrite.Should().ThrowAsync<PostgresException>().WithMessage("*final*");
        var asProvider = () => ExecAsync($"UPDATE payments.refunds SET provider_refund_id = 're_fake' WHERE id = '{refundId}'");
        await asProvider.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task ManualRefund_ConstraintsRejectHalfRecordsAndIllegalTransitions()
    {
        var (_, refundId) = await NeedsReviewManualRefundAsync();
        var noWho = () => ExecAsync($"UPDATE payments.refunds SET state = 'ManuallyRecorded' WHERE id = '{refundId}'");
        await noWho.Should().ThrowAsync<PostgresException>().WithMessage("*chk_refunds_manual_fields*");
        var strayText = () => ExecAsync($"UPDATE payments.refunds SET manual_note = 'x' WHERE id = '{refundId}'");
        await strayText.Should().ThrowAsync<PostgresException>().WithMessage("*chk_refunds_manual_fields*");

        // Only a refund waiting for review can be recorded: a Requested one cannot, even with every field present.
        _provider.SupportsRefunds = false;
        var early = await RefundRequiredOrderAsync();
        var (earlyId, _) = await RefundIdentityAsync(early.OrderId);
        var skip = () => ExecAsync($"UPDATE payments.refunds SET state = 'ManuallyRecorded', manual_recorded_by = '{_admin.Id}', manual_recorded_at_utc = now() WHERE id = '{earlyId}'");
        await skip.Should().ThrowAsync<PostgresException>().WithMessage("*waiting for review*");
    }

    [Fact]
    public async Task ManualRefund_StripeLikeProviderConfirmedRefund_IsUnchanged()
    {
        _provider.Mode = RefundMode.Succeed;
        var order = await RefundRequiredOrderAsync();
        await RunAsync();
        var (refundId, key) = await RefundIdentityAsync(order.OrderId);

        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Succeeded");
        (await StringAsync($"SELECT COALESCE(manual_recorded_by::text, 'none') FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("none");
        _provider.RefundCalls(key).Should().Be(1);
        await AssertProblemAsync(await RecordManualAsync(refundId, await ManualBodyAsync(refundId)), HttpStatusCode.Conflict, "Operations.ManualRefundNotAllowed");
    }

    // The next tests make contention observable (a row lock held on a separate connection until PostgreSQL reports every caller
    // waiting; see PostgresRaceKit). They assert invariants for EITHER outcome and read the persisted rows. Compiled, NOT executed.

    [Fact]
    public async Task RetryAndManualCompletion_ContendingForTheSameRefund_LeaveExactlyOneDecision_AndNoProviderCall()
    {
        var (_, refundId) = await NeedsReviewManualRefundAsync();
        var body = await ManualBodyAsync(refundId);

        var codes = await PostgresRace.RaceBehindLockAsync(_database.ConnectionString, $"SELECT 1 FROM payments.refunds WHERE id = '{refundId}' FOR UPDATE",
            async () => (await _admin.Client.PostAsync($"/api/admin/payments/refunds/{refundId}/retry", null)).StatusCode,
            async () => (await RecordManualAsync(refundId, body)).StatusCode);
        var (retry, manual) = (codes[0], codes[1]);

        var state = await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'");
        var retries = await ScalarAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{refundId}' AND action = 'RetryRefund'");
        var manuals = await ScalarAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{refundId}' AND action = 'RecordManualRefund'");
        if (retry == HttpStatusCode.NoContent)
            (manual, state, retries, manuals).Should().Be((HttpStatusCode.Conflict, "Unknown", 1L, 0L), "the retry won: the manual record is refused");
        else
            (retry, manual, state, retries, manuals).Should().Be((HttpStatusCode.Conflict, HttpStatusCode.OK, "ManuallyRecorded", 0L, 1L), "the manual record won: the retry is refused");
        (await ScalarAsync($"SELECT count(*) FROM payments.refunds WHERE id = '{refundId}' AND state = 'Succeeded'")).Should().Be(0, "nothing here can be provider-confirmed");
        _provider.MoneyMoved(refundId).Should().Be(0);

        await RunAsync(); // a worker pass afterwards never overwrites a manual record and returns an Unknown manual-only refund to review
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be(state == "Unknown" ? "NeedsReview" : "ManuallyRecorded");
        _provider.MoneyMoved(refundId).Should().Be(0);
    }

    [Fact]
    public async Task AWorkerSkipsARefundThatIsLockedByAnAdministrator_AndNeverOverwritesTheDecision()
    {
        var (_, refundId) = await NeedsReviewManualRefundAsync();
        (await _admin.Client.PostAsync($"/api/admin/payments/refunds/{refundId}/retry", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("Unknown");

        await using (var held = await RowLock.AcquireAsync(_database.ConnectionString, $"SELECT 1 FROM payments.refunds WHERE id = '{refundId}' FOR UPDATE"))
        {
            await RunAsync().WaitAsync(PostgresRace.Timeout); // FOR UPDATE SKIP LOCKED: the worker does not wait for, or touch, a locked row
            (await ScalarAsync($"SELECT attempts FROM payments.refunds WHERE id = '{refundId}'")).Should().Be(0, "the locked refund was not claimed");
            await held.ReleaseAsync();
        }

        // While it is Unknown the manual record is refused; once the worker escalates it to review it is allowed, exactly once.
        await AssertProblemAsync(await RecordManualAsync(refundId, await ManualBodyAsync(refundId)), HttpStatusCode.Conflict, "Operations.ManualRefundNotAllowed");
        await RunAsync();
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("NeedsReview");
        (await RecordManualAsync(refundId, await ManualBodyAsync(refundId))).StatusCode.Should().Be(HttpStatusCode.OK);
        await RunAsync();
        await RunAsync();
        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("ManuallyRecorded", "workers never overwrite a terminal administrator decision");
        _provider.MoneyMoved(refundId).Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentAdministratorRefundRequests_ForTheSameOrder_CreateOneRefundAndOneAuditRow()
    {
        _provider.Mode = RefundMode.Pending;
        var order = await RefundRequiredOrderAsync();
        await ExecAsync("ALTER TABLE payments.refunds DISABLE TRIGGER trg_refunds_guard_update");
        await ExecAsync($"DELETE FROM payments.refunds WHERE order_id = '{order.OrderId}'");
        await ExecAsync("ALTER TABLE payments.refunds ENABLE TRIGGER trg_refunds_guard_update");

        var codes = await PostgresRace.RaceBehindLockAsync(_database.ConnectionString, $"SELECT 1 FROM payments.orders WHERE id = '{order.OrderId}' FOR UPDATE",
            async () => (await _admin.Client.PostAsync($"/api/admin/payments/orders/{order.OrderId}/refund", null)).StatusCode,
            async () => (await _admin.Client.PostAsync($"/api/admin/payments/orders/{order.OrderId}/refund", null)).StatusCode);

        codes.Should().OnlyContain(c => c == HttpStatusCode.OK);
        (await ScalarAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be(1, "one refund per order is decided by the database");
        (await ScalarAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{order.OrderId}' AND action = 'RequestRefund'")).Should().Be(1);
    }

    private async Task<List<string>> RefundHistoryAsync(Guid orderId)
    {
        await using var db = _database.CreateReservationDbContext();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText =
            "SELECT entity_type || '|' || COALESCE(from_state, 'new') || '|' || to_state || '|' || source || '|' || fact_basis || '|' || COALESCE(actor_user_id::text, '') || '|' || COALESCE(reason, '') " +
            $"FROM payments.financial_transitions WHERE order_id = '{orderId}' AND entity_type IN ('Refund', 'Order') ORDER BY occurred_at_utc, id";
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    [Fact]
    public async Task History_KeepsAManualRefundApartFromAProviderConfirmedOne()
    {
        // A refund the provider confirmed: the worker's move to Succeeded rests on the provider's answer.
        _provider.Mode = RefundMode.Succeed;
        var confirmed = await RefundRequiredOrderAsync();
        await RunAsync();
        var confirmedRows = await RefundHistoryAsync(confirmed.OrderId);
        confirmedRows.Should().Contain(r => r.StartsWith("Refund|Unknown|Succeeded|Worker|ProviderVerified|") && r.EndsWith("|provider_answer"));
        confirmedRows.Should().NotContain(r => r.Contains("AdministratorStatement"));

        // A refund an administrator recorded: a statement, with the administrator as actor, and never a provider fact.
        var (order, refundId) = await NeedsReviewManualRefundAsync();
        (await RecordManualAsync(refundId, await ManualBodyAsync(refundId))).StatusCode.Should().Be(HttpStatusCode.OK);
        var manualRows = await RefundHistoryAsync(order.OrderId);

        manualRows.Should().Contain($"Refund|NeedsReview|ManuallyRecorded|Administrator|AdministratorStatement|{_admin.Id}|manual_refund_statement");
        manualRows.Should().Contain($"Order|RefundRequired|Refunded|Administrator|AdministratorStatement|{_admin.Id}|manual_refund_statement");
        manualRows.Should().NotContain(r => r.Contains("ProviderVerified") && r.Contains("ManuallyRecorded"));
        manualRows.Should().Contain(r => r.StartsWith("Refund|Requested|NeedsReview|Worker|ApplicationDecision|") && r.EndsWith("|needs_operator"), "the escalation is an application decision, not a provider fact");
        _provider.MoneyMoved(refundId).Should().Be(0);
    }

    [Fact]
    public async Task ManualRefund_NeedsAnAdministratorAndACsrfToken()
    {
        var (_, refundId) = await NeedsReviewManualRefundAsync();
        var path = $"/api/admin/payments/refunds/{refundId}/manual-completion";
        var body = await ManualBodyAsync(refundId);

        var anonymous = _factory.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(anonymous);
        await AssertProblemAsync(await anonymous.PostAsJsonAsync(path, body), HttpStatusCode.Unauthorized, "Auth.Unauthorized");
        var customer = await AuthTestSupport.RegisterAsync(_factory);
        await AssertProblemAsync(await customer.Client.PostAsJsonAsync(path, body), HttpStatusCode.Forbidden, "Auth.Forbidden");

        var admin = await AdminTestSupport.RegisterAdminAsync(_factory);
        admin.Client.DefaultRequestHeaders.Remove(AuthTestSupport.CsrfHeader);
        await AssertProblemAsync(await admin.Client.PostAsJsonAsync(path, body), HttpStatusCode.Forbidden, "Csrf.Invalid");

        (await StringAsync($"SELECT state FROM payments.refunds WHERE id = '{refundId}'")).Should().Be("NeedsReview");
    }

    // ───────────── helpers ─────────────

    private async Task<(TestUser User, OnSaleEventBuilder.SaleSetup Sale, OrderResponse Started)> StartedPaymentAsync(CatalogApiFactory? host = null, int seats = 1)
    {
        host ??= _factory;
        var admin = ReferenceEquals(host, _factory) ? _admin : await AdminTestSupport.RegisterAdminAsync(host);
        var sale = await OnSaleEventBuilder.CreateAsync(admin.Client, seatCount: Math.Max(seats, 1));
        var user = await AuthTestSupport.RegisterAsync(host);
        var order = await CheckoutAsync(user, await HoldAsync(user, sale, Enumerable.Range(0, seats).ToArray()));
        var response = await user.Client.SendAsync(Post($"/api/orders/{order.OrderId}/payments", null, Guid.NewGuid().ToString()));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var started = (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
        _users[started.OrderId] = user;
        return (user, sale, started);
    }

    private readonly ConcurrentDictionary<Guid, TestUser> _users = new();

    private Task<TestUser> UserOfAsync(OrderResponse order) => Task.FromResult(_users[order.OrderId]);

    /// <summary>A payment that succeeded at the provider, whose hold had already expired: the order requires a refund.</summary>
    private async Task<OrderResponse> RefundRequiredOrderAsync(CatalogApiFactory? host = null)
    {
        var (user, _, started) = await StartedPaymentAsync(host);
        await ExpireReservationAsync(started.ReservationId);
        // Make the late verdict arrive through the normal callback so the system itself creates the refund record.
        var response = await CompleteAsync(user, started, "succeed");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{started.OrderId}'")).Should().Be("RefundRequired");
        (await StringAsync($"SELECT state FROM payments.refunds WHERE order_id = '{started.OrderId}'")).Should().Be("Requested");
        return started;
    }

    private async Task<(Guid RefundId, string Key)> RefundIdentityAsync(Guid orderId)
    {
        var id = Guid.Parse((await StringAsync($"SELECT id FROM payments.refunds WHERE order_id = '{orderId}'"))!);
        var submission = await ScalarAsync($"SELECT submission_number FROM payments.refunds WHERE id = '{id}'");
        return (id, $"refund-{id:N}-{submission}");
    }

    private async Task CrashAfterVerdictAsync(OrderResponse started) =>
        await ExecAsync($"UPDATE payments.payment_attempts SET status = 'Succeeded', completed_at_utc = now() - interval '1 minute' WHERE id = '{started.Payment!.AttemptId}'");

    private async Task ExpireReservationAsync(Guid reservationId)
    {
        await ExecAsync($"UPDATE reservation.reservations SET created_at_utc = now() - interval '20 minutes', expires_at_utc = now() - interval '10 minutes' WHERE id = '{reservationId}'");
        await ExecAsync($"UPDATE reservation.event_seats SET hold_expires_at_utc = now() - interval '10 minutes' WHERE reservation_id = '{reservationId}'");
    }

    private async Task<WorkflowRunResult> RunAsync(CatalogApiFactory? host = null)
    {
        await using var scope = (host ?? _factory).Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsWorkflow>().RunOnceAsync();
    }

    private static HttpRequestMessage Post(string path, object? body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path) =>
        client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

    private static async Task<ReservationResponse> HoldAsync(TestUser user, OnSaleEventBuilder.SaleSetup sale, params int[] seatIndexes)
    {
        var response = await user.Client.SendAsync(Post("/api/reservations", new { eventId = sale.EventId, seatIds = seatIndexes.Select(i => sale.SeatIds[i]).ToArray() }, Guid.NewGuid().ToString()));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReservationResponse>())!;
    }

    private static async Task<OrderResponse> CheckoutAsync(TestUser user, ReservationResponse reservation)
    {
        var response = await user.Client.SendAsync(Post("/api/orders", new { reservationId = reservation.ReservationId }, Guid.NewGuid().ToString()));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private static Task<HttpResponseMessage> CompleteAsync(TestUser user, OrderResponse started, string outcome) =>
        user.Client.SendAsync(Post("/api/dev/payment-simulator/complete",
            new { orderId = started.OrderId, attemptId = started.Payment!.AttemptId, simulatorToken = started.Payment.SimulatorToken, outcome }, null));

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
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
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

public class PaymentWorkflowConfigurationTests
{
    private sealed class Env(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Config(params (string, string)[] values) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void DevNotificationAdapter_OutsideDevelopmentAndTesting_FailsStartup(string environment)
    {
        var act = () => PulseReserve.AppHost.Configuration.PaymentsOptions.Resolve(Config(("Notifications:DevAdapter:Enabled", "true")), new Env(environment));
        act.Should().Throw<InvalidOperationException>().WithMessage("*notification adapter*");
    }

    [Fact]
    public void Defaults_RunTheWorkersAndKeepBothDevAdaptersOff_InProduction()
    {
        var options = PulseReserve.AppHost.Configuration.PaymentsOptions.Resolve(Config(), new Env("Production"));
        options.WorkersEnabled.Should().BeTrue();
        options.SimulatorEnabled.Should().BeFalse();
        options.DevNotificationsEnabled.Should().BeFalse();
        options.Workflow.MaxAttempts.Should().Be(8);
        options.Workflow.BatchSize.Should().Be(20);
    }

    [Theory]
    [InlineData("Payments:Workflow:BatchSize", "0")]
    [InlineData("Payments:Workflow:MaxAttempts", "0")]
    [InlineData("Payments:Workflow:LeaseSeconds", "0")]
    [InlineData("Payments:Workers:IntervalSeconds", "-1")]
    [InlineData("Payments:Workers:Enabled", "maybe")]
    public void InvalidWorkflowSettings_FailStartup(string key, string value)
    {
        var act = () => PulseReserve.AppHost.Configuration.PaymentsOptions.Resolve(Config((key, value)), new Env("Production"));
        act.Should().Throw<InvalidOperationException>();
    }
}
