using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// The webhook acknowledgement matrix, exercised through the REAL HTTP endpoint (in-memory test server, real middleware, real provider
/// adapters, real signatures): which status the provider receives, and what is durably stored when it does. The rule behind every row:
/// <list type="bullet">
/// <item>2xx only when the event needs no redelivery - applied, a harmless duplicate, or its evidence is DURABLY recorded;</item>
/// <item>4xx when the event is not authentic or not readable (nothing is stored, nothing is trusted);</item>
/// <item>5xx when it could not be recorded or judged, so the provider delivers it again.</item>
/// </list>
/// Stripe retries any non-2xx for up to three days in live mode (three attempts over a few hours in a sandbox); Paymob documents no retry
/// policy at all. Neither is assumed: the only answer that is safe for both is "2xx after the record exists, otherwise not 2xx".
/// These run against in-memory stores; the SQL behind the recorder is covered by the (not yet executed) PostgreSQL integration tests.
/// </summary>
public sealed class WebhookAcknowledgementTests
{
    private static InMemoryOrderStore.AttemptRow AddAttempt(PaymentHttpHost host, string provider = "Stripe", string reference = "cs_test_ack")
    {
        var row = new InMemoryOrderStore.AttemptRow
        {
            Id = Guid.NewGuid(), OrderId = host.Store.Order.OrderId, Number = 1, Key = "key-" + Guid.NewGuid(), Provider = provider, Reference = reference
        };
        host.Store.Attempts.Add(row);
        return row;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> SendAsync(PaymentHttpHost host, HttpRequestMessage request)
    {
        using var response = await host.Client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        JsonElement body = default;
        if (raw.Length > 0 && response.Content.Headers.ContentType?.MediaType is "application/json" or "application/problem+json")
            body = JsonDocument.Parse(raw).RootElement.Clone();
        return (response.StatusCode, body, raw);
    }

    private static void NothingWasApplied(PaymentHttpHost host, InMemoryOrderStore.AttemptRow attempt)
    {
        host.Store.Order.Status.Should().Be(OrderStatus.PendingPayment);
        attempt.Status.Should().Be(PaymentAttemptStatus.Initiated);
        host.Store.FinalizeCalls.Should().Be(0);
        host.Store.PaidTransitions.Should().Be(0);
        host.Store.RefundRequiredTransitions.Should().Be(0);
        host.Store.Exceptions.Should().BeEmpty();
    }

    // ───────────── 1. not authentic: refused, nothing created ─────────────

    [Fact]
    public async Task AnInvalidSignature_IsRefusedWith400_AndCreatesNeitherAnAnomalyNorAnyStateChange()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var attempt = AddAttempt(host);

        // Content that WOULD be an anomaly if it were authentic: wrong amount.
        var (status, body, _) = await SendAsync(host, host.Webhook.Stripe(attempt, amount: 5, signature: "t=1,v1=00"));

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("reason").GetString().Should().Be("invalid_signature");
        host.Anomalies.Records.Should().BeEmpty("an unauthenticated event proves nothing, so it must not create a record an operator would trust");
        NothingWasApplied(host, attempt);
    }

    [Fact]
    public async Task AForgedPaymobCallback_WithAWrongHmac_IsRefusedWith400_AndCreatesNothing()
    {
        await using var host = await PaymentHttpHost.StartAsync(stripe: false, paymob: true);
        var attempt = AddAttempt(host, "Paymob", "217503754");

        var (status, body, _) = await SendAsync(host, host.Webhook.Paymob(attempt, cents: 100, hmac: "0000"));

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("reason").GetString().Should().Be("invalid_signature");
        host.Anomalies.Records.Should().BeEmpty();
        NothingWasApplied(host, attempt);
    }

    // ───────────── 2. not readable: a client error, no mutation ─────────────

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"id\":\"evt_x\"}")]
    [InlineData("")]
    public async Task ASignedButStructurallyInvalidBody_IsA400_AndMutatesNothing(string body)
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var attempt = AddAttempt(host);

        var (status, answer, _) = await SendAsync(host, host.Webhook.StripeBody(body));

        status.Should().Be(HttpStatusCode.BadRequest);
        answer.GetProperty("status").GetString().Should().Be("rejected");
        host.Anomalies.Records.Should().BeEmpty();
        NothingWasApplied(host, attempt);
    }

    [Fact]
    public async Task AnEventOfAnotherMerchantContext_IsRefusedWith400_NotAcknowledged_AndCreatesNothing()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var attempt = AddAttempt(host);

        // A live-mode event arriving at a deployment configured for test mode (or an event of a connected account): the endpoint is
        // registered in the wrong place. Acknowledging it would hide possibly REAL money; refusing it keeps it visible (and resendable)
        // in the provider's dashboard until the registration is fixed.
        var (status, body, _) = await SendAsync(host, host.Webhook.Stripe(attempt, livemode: true));

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("reason").GetString().Should().Be("wrong_context");
        host.Anomalies.Records.Should().BeEmpty("it is not evidence about OUR payments");
        NothingWasApplied(host, attempt);
    }

    // ───────────── 3. matching: the normal workflow ─────────────

    [Fact]
    public async Task AVerifiedMatchingSuccess_FollowsTheNormalWorkflow_With200_AndRecordsNoAnomaly()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var attempt = AddAttempt(host);

        var (status, body, _) = await SendAsync(host, host.Webhook.Stripe(attempt));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("status").GetString().Should().Be("applied");
        host.Store.FinalizeCalls.Should().Be(1);
        host.Store.PaidTransitions.Should().Be(1);
        host.Anomalies.Records.Should().BeEmpty();
    }

    // ───────────── 4. verified mismatch: recorded, committed, THEN acknowledged ─────────────

    [Fact]
    public async Task AVerifiedMismatch_IsRecordedFirst_ThenAcknowledgedWith200_AndNothingIsApplied()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var attempt = AddAttempt(host);
        var recordedWhenAnswered = -1;
        var request = host.Webhook.Stripe(attempt, amount: 100, eventId: "evt_ack_1");

        var (status, body, raw) = await SendAsync(host, request);
        recordedWhenAnswered = host.Anomalies.Records.Count;

        status.Should().Be(HttpStatusCode.OK, raw);
        body.GetProperty("status").GetString().Should().Be("recorded_for_review");
        body.GetProperty("reason").GetString().Should().Be("amount_or_currency_mismatch");
        recordedWhenAnswered.Should().Be(1, "the acknowledgement is only sent once the evidence exists");
        var record = host.Anomalies.Records.Single();
        (record.Kind, record.ProviderEventId, record.AttemptId, record.OrderId).Should().Be(
            (PaymentAnomalyKind.AmountOrCurrencyMismatch, "evt_ack_1", attempt.Id, host.Store.Order.OrderId));
        NothingWasApplied(host, attempt);
    }

    [Fact]
    public async Task AVerifiedPaymobMismatch_IsRecordedAndAcknowledgedWith200_AsWell()
    {
        await using var host = await PaymentHttpHost.StartAsync(stripe: false, paymob: true);
        var attempt = AddAttempt(host, "Paymob", "217503754");

        var (status, body, raw) = await SendAsync(host, host.Webhook.Paymob(attempt, cents: 100));

        status.Should().Be(HttpStatusCode.OK, raw);
        body.GetProperty("status").GetString().Should().Be("recorded_for_review");
        host.Anomalies.Records.Should().ContainSingle().Which.Provider.Should().Be("Paymob");
        NothingWasApplied(host, attempt);
    }

    [Fact]
    public async Task AReferenceThatDoesNotMatchTheStoredOne_IsRecordedAndAcknowledged_NotRetried()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var attempt = AddAttempt(host);
        attempt.Reference = "cs_the_one_we_stored";
        var wrong = new InMemoryOrderStore.AttemptRow { Id = attempt.Id, OrderId = attempt.OrderId, Number = 1, Key = "x", Provider = "Stripe", Reference = "cs_someone_elses" };

        var (status, body, _) = await SendAsync(host, host.Webhook.Stripe(wrong));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("reason").GetString().Should().Be("reference_mismatch");
        host.Anomalies.Records.Should().ContainSingle().Which.Kind.Should().Be(PaymentAnomalyKind.ReferenceMismatch);
        NothingWasApplied(host, attempt);
    }

    // ───────────── 5. persistence failure: never acknowledged, retried, then recorded ─────────────

    [Fact]
    public async Task APersistenceFailure_IsNotAcknowledged_LeaksNothing_AndTheProvidersRetryIsRecordedAndAcknowledged()
    {
        await using var host = await PaymentHttpHost.StartAsync(environment: "Production");
        var attempt = AddAttempt(host);
        host.Anomalies.Fail = true;

        var first = await SendAsync(host, host.Webhook.Stripe(attempt, amount: 100, eventId: "evt_retry_1"));

        first.Status.Should().Be(HttpStatusCode.InternalServerError, "the provider must be told to deliver the event again");
        first.Raw.Should().Contain("Server.Error")
            .And.NotContain("storage unavailable").And.NotContain("InvalidOperationException").And.NotContain("   at ")
            .And.NotContain("evt_retry_1").And.NotContain(attempt.Id.ToString());
        host.Anomalies.Records.Should().BeEmpty("nothing was durably stored, so nothing may look acknowledged");
        NothingWasApplied(host, attempt);

        host.Anomalies.Fail = false; // the database is back; the provider delivers the same event again
        var retry = await SendAsync(host, host.Webhook.Stripe(attempt, amount: 100, eventId: "evt_retry_1"));

        retry.Status.Should().Be(HttpStatusCode.OK);
        retry.Body.GetProperty("status").GetString().Should().Be("recorded_for_review");
        host.Anomalies.Records.Should().ContainSingle();
        NothingWasApplied(host, attempt);
    }

    [Fact]
    public async Task AFailureBeforeAnythingCouldBeJudged_IsNotAcknowledgedEither()
    {
        await using var host = await PaymentHttpHost.StartAsync(environment: "Production");
        var attempt = AddAttempt(host);
        host.Store.LookupsFail = true;

        var (status, _, raw) = await SendAsync(host, host.Webhook.Stripe(attempt));

        status.Should().Be(HttpStatusCode.InternalServerError);
        raw.Should().NotContain("storage unavailable");
        host.Anomalies.Records.Should().BeEmpty();
        NothingWasApplied(host, attempt);
    }

    [Fact]
    public async Task AnOrderThatCannotBeLoadedOrJudged_IsNotAcknowledged_AndNothingIsInventedAboutIt()
    {
        await using var host = await PaymentHttpHost.StartAsync(environment: "Production");
        var attempt = AddAttempt(host);
        host.Store.OrderUnreadable = true;

        var (status, body, _) = await SendAsync(host, host.Webhook.Stripe(attempt));

        status.Should().Be(HttpStatusCode.InternalServerError);
        body.GetProperty("reason").GetString().Should().Be("order_not_found");
        host.Anomalies.Records.Should().BeEmpty("a record needs trustworthy expected values; the provider keeps the event and delivers it again");
        host.Store.FinalizeCalls.Should().Be(0);
    }

    [Fact]
    public async Task AnOrderWhoseAmountCannotBeExpressedInMinorUnits_IsNotAcknowledged()
    {
        await using var host = await PaymentHttpHost.StartAsync(environment: "Production");
        var attempt = AddAttempt(host);
        host.Store.Order = host.Store.Order with { TotalAmount = 91.005m };

        var (status, body, _) = await SendAsync(host, host.Webhook.Stripe(attempt));

        status.Should().Be(HttpStatusCode.InternalServerError);
        body.GetProperty("reason").GetString().Should().Be("order_amount_invalid");
        host.Anomalies.Records.Should().BeEmpty();
        host.Store.FinalizeCalls.Should().Be(0);
    }

    // ───────────── 6. duplicates ─────────────

    [Fact]
    public async Task ARedeliveredMismatch_IsDeduplicated_AndAcknowledgedEveryTime()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var attempt = AddAttempt(host);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await SendAsync(host, host.Webhook.Stripe(attempt, amount: 100, eventId: "evt_dup_1"))).Status);

        statuses.Should().OnlyContain(s => s == HttpStatusCode.OK, "once the record exists a redelivery must not be retried again");
        host.Anomalies.Records.Should().ContainSingle();
        NothingWasApplied(host, attempt);
    }

    [Fact]
    public async Task ADifferentMismatchEvent_ForTheSameAttempt_IsAnotherRecord()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var attempt = AddAttempt(host);

        await SendAsync(host, host.Webhook.Stripe(attempt, amount: 100, eventId: "evt_a"));
        await SendAsync(host, host.Webhook.Stripe(attempt, amount: 100, eventId: "evt_b"));

        host.Anomalies.Records.Select(r => r.ProviderEventId).Should().BeEquivalentTo("evt_a", "evt_b");
    }

    // ───────────── 7. uncorrelatable ─────────────

    [Fact]
    public async Task AVerifiedSuccessForAnUnknownAttempt_KeepsMinimalEvidenceWithoutInventingAnAssociation_AndIsAcknowledged()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var phantom = host.Webhook.Phantom();

        var (status, body, _) = await SendAsync(host, host.Webhook.Stripe(phantom, amount: 4200, eventId: "evt_unknown_1"));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("reason").GetString().Should().Be("unknown_attempt");
        var record = host.Anomalies.Records.Should().ContainSingle().Subject;
        record.Kind.Should().Be(PaymentAnomalyKind.UnknownPaymentReference);
        (record.AttemptId, record.OrderId).Should().Be(((Guid?)null, (Guid?)null), "nothing about an unknown event is allowed to look like one of our orders");
        record.ClaimedAttemptId.Should().Be(phantom.Id);
        (record.ReceivedAmountMinor, record.ExpectedAmountMinor).Should().Be((4200L, (long?)null));
        host.Store.Attempts.Should().BeEmpty();
        host.Store.FinalizeCalls.Should().Be(0);
    }

    [Fact]
    public async Task AVerifiedFailureForAnUnknownAttempt_MovedNoMoney_SoItIsAcknowledgedAndIgnored_NotRetriedForDays()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var phantom = host.Webhook.Phantom();

        var (status, body, _) = await SendAsync(host, host.Webhook.Stripe(phantom, type: "checkout.session.expired", paymentStatus: "unpaid"));

        status.Should().Be(HttpStatusCode.OK, "another environment's expired session on a shared provider account can never become processable here");
        body.GetProperty("status").GetString().Should().Be("unmatched_event_ignored");
        host.Anomalies.Records.Should().BeEmpty("no money moved, so there is no evidence to keep");
    }

    // ───────────── 8. conflicting local state ─────────────

    [Fact]
    public async Task AConflictingVerdictForACompletedAttempt_IsAcknowledged_AndNothingIsOverwritten()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var attempt = AddAttempt(host);
        (await SendAsync(host, host.Webhook.Stripe(attempt))).Status.Should().Be(HttpStatusCode.OK);
        attempt.Status.Should().Be(PaymentAttemptStatus.Succeeded);

        var (status, body, _) = await SendAsync(host, host.Webhook.Stripe(attempt, type: "checkout.session.expired", paymentStatus: "unpaid", eventId: "evt_expired_late"));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("status").GetString().Should().Be("conflicts_with_recorded_outcome");
        attempt.Status.Should().Be(PaymentAttemptStatus.Succeeded, "a recorded success is never rewritten");
        host.Store.PaidTransitions.Should().Be(1);
    }

    [Fact]
    public void EveryWebhookOutcome_HasAnExplicitHttpMapping_SoANewOneCannotBecomeASilent4xxOrAn2xx()
    {
        // Adding an outcome must be a deliberate decision: map it in PaymentProviderEndpoints, add a row to the matrix in
        // docs/architecture/21-payment-anomalies.md (section 4b) and a case above, then extend this list.
        Enum.GetValues<PulseReserve.Modules.Payments.Features.WebhookOutcome>().Should().BeEquivalentTo(
        [
            PulseReserve.Modules.Payments.Features.WebhookOutcome.Applied, PulseReserve.Modules.Payments.Features.WebhookOutcome.Acknowledged,
            PulseReserve.Modules.Payments.Features.WebhookOutcome.AnomalyRecorded, PulseReserve.Modules.Payments.Features.WebhookOutcome.ProviderUnavailable,
            PulseReserve.Modules.Payments.Features.WebhookOutcome.Rejected, PulseReserve.Modules.Payments.Features.WebhookOutcome.Failed
        ]);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PulseReserve.sln")))
            root = root.Parent;
        var endpoint = File.ReadAllText(Path.Combine(root!.FullName, "src", "PulseReserve.AppHost", "Api", "PaymentProviderEndpoints.cs"));
        foreach (var outcome in Enum.GetNames<PulseReserve.Modules.Payments.Features.WebhookOutcome>())
            endpoint.Should().Contain($"WebhookOutcome.{outcome}", $"{outcome} is mapped explicitly");
        endpoint.Should().Contain("has no HTTP mapping", "an unmapped outcome fails closed instead of defaulting to a status");
    }

    [Fact]
    public async Task EveryAnswerBodyCarriesOnlyStatusAndReasonCodes()
    {
        await using var host = await PaymentHttpHost.StartAsync(environment: "Production");
        var attempt = AddAttempt(host);
        var answers = new[]
        {
            await SendAsync(host, host.Webhook.Stripe(attempt, amount: 100, eventId: "evt_shape_1")),
            await SendAsync(host, host.Webhook.Stripe(host.Webhook.Phantom(), eventId: "evt_shape_2")),
            await SendAsync(host, host.Webhook.Stripe(attempt, signature: "t=1,v1=00")),
            await SendAsync(host, host.Webhook.StripeBody("nope"))
        };

        foreach (var answer in answers)
        {
            answer.Body.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(["status", "reason"]);
            answer.Raw.Should().NotContain(attempt.Id.ToString()).And.NotContain("cs_test_ack").And.NotContain(TestProviders.StripeWebhookSecret);
        }
    }
}
