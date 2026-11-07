using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// The operator side of payment anomalies and of the financial history: service rules, the HTTP pipeline (authorization,
/// antiforgery, binding, filters) and what is exposed. The stores are scripted, so none of this proves SQL, locking or triggers.
/// </summary>
public sealed class PaymentAnomalyOperationsTests
{
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid AnomalyId = Guid.Parse("dddddddd-0000-4000-8000-000000000001");
    private static readonly string Route = $"/api/admin/payments/anomalies/{AnomalyId}/resolve";

    private static PaymentAnomaly Anomaly(string state = PaymentAnomalyState.Open, string kind = PaymentAnomalyKind.AmountOrCurrencyMismatch) => new(
        AnomalyId, kind, "amount_or_currency_mismatch", state, "Stripe", "evt_1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        1, "USD", 9100, "USD", OrderStatus.PendingPayment, PaymentAttemptStatus.Initiated, DateTime.UtcNow.AddMinutes(-3), null, null, null, null, null, OrderStatus.PendingPayment)
    {
        AgeSeconds = 180
    };

    private static PaymentAnomalyResolutionRecord Valid(string resolution = PaymentAnomalyResolution.RefundedExternally) => new(true, resolution, "re_dashboard_1", "checked in the dashboard");

    private static (PaymentOperationsService Service, DispatchFake Store, List<PaymentAnomalyResolutionRecord> Recorded) Build(PaymentAnomalyResolveOutcome outcome)
    {
        var store = DispatchFake.Create<IWorkflowStore>(out var fake);
        var recorded = new List<PaymentAnomalyResolutionRecord>();
        fake.Handlers["ResolveAnomalyAsync"] = a =>
        {
            recorded.Add((PaymentAnomalyResolutionRecord)a[2]!);
            return new PaymentAnomalyResolveResult(outcome, outcome == PaymentAnomalyResolveOutcome.NotFound ? null : Anomaly(PaymentAnomalyState.Resolved));
        };
        return (new PaymentOperationsService(store, new PaymentProviderRegistry([new ScriptedProvider("Stripe", false, "USD")])), fake, recorded);
    }

    // ───────────── what an anomaly says ─────────────

    [Fact]
    public void TheOnlySupportedNextStep_IsDerivedFromTheState()
    {
        Anomaly().NextAction.Should().Be(PaymentAnomalyAction.InvestigateThenRecord);
        Anomaly(PaymentAnomalyState.Resolved).NextAction.Should().Be(PaymentAnomalyAction.None);
    }

    [Fact]
    public void AnAnomaly_KeepsTrustedAndUntrustedValuesApart_AndCarriesNoPayloadSignatureSecretReferenceOrCustomerDetail()
    {
        var names = typeof(PaymentAnomaly).GetProperties().Select(p => p.Name).ToArray();

        names.Should().Contain(["ReceivedAmountMinor", "ReceivedCurrency", "ClaimedAttemptId", "ClaimedOrderId"], "what the event said is labelled as received/claimed");
        names.Should().Contain(["ExpectedAmountMinor", "ExpectedCurrency", "AttemptId", "OrderId"], "what we loaded from our own records");
        names.Should().NotContain(n => n.Contains("Signature") || n.Contains("Secret") || n.Contains("Payload") || n.Contains("Customer") || n.Contains("CheckoutUrl") || n.Contains("ProviderReference"));
    }

    // ───────────── resolution rules ─────────────

    [Fact]
    public async Task AResolutionNeedsTheExplicitConfirmation_AndTouchesNothingWithoutIt()
    {
        var (service, store, recorded) = Build(PaymentAnomalyResolveOutcome.Resolved);

        var result = await service.ResolveAnomalyAsync(AnomalyId, Valid() with { InvestigationConfirmed = false }, Admin);

        result.Error.Should().Be(PaymentsErrors.AnomalyNotConfirmed);
        store.Calls.Should().BeEmpty();
        recorded.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Refunded")]
    [InlineData("MarkPaid")]
    [InlineData("noactionneeded")]
    public async Task OnlyTheFixedStatements_AreAccepted_NeverAnActionOnTheOrder(string resolution)
    {
        var (service, store, _) = Build(PaymentAnomalyResolveOutcome.Resolved);

        var result = await service.ResolveAnomalyAsync(AnomalyId, Valid(resolution), Admin);

        result.Error.Should().Be(PaymentsErrors.AnomalyResolutionInvalid);
        store.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task OverlongText_IsRejected()
    {
        var (service, _, _) = Build(PaymentAnomalyResolveOutcome.Resolved);

        (await service.ResolveAnomalyAsync(AnomalyId, Valid() with { ExternalReference = new string('x', 201) }, Admin)).Error.Should().Be(PaymentsErrors.AnomalyResolutionInvalid);
        (await service.ResolveAnomalyAsync(AnomalyId, Valid() with { Note = new string('x', 301) }, Admin)).Error.Should().Be(PaymentsErrors.AnomalyResolutionInvalid);
    }

    [Theory]
    [InlineData(PaymentAnomalyResolveOutcome.NotFound, "Operations.NotFound")]
    [InlineData(PaymentAnomalyResolveOutcome.Conflict, "Operations.AnomalyResolutionConflict")]
    public async Task TheStoresVerdictUnderTheRowLock_IsReportedAsFailure(PaymentAnomalyResolveOutcome outcome, string code)
    {
        var (service, _, _) = Build(outcome);

        (await service.ResolveAnomalyAsync(AnomalyId, Valid(), Admin)).Error.Code.Should().Be(code);
    }

    [Theory]
    [InlineData(PaymentAnomalyResolveOutcome.Resolved)]
    [InlineData(PaymentAnomalyResolveOutcome.Replay)]
    public async Task ARecordedOrReplayedStatement_IsASuccess_ThatStillShowsAsAnAdministratorStatement(PaymentAnomalyResolveOutcome outcome)
    {
        var (service, store, recorded) = Build(outcome);

        var result = await service.ResolveAnomalyAsync(AnomalyId, Valid(), Admin);

        result.IsSuccess.Should().BeTrue();
        result.Value.State.Should().Be(PaymentAnomalyState.Resolved);
        recorded.Should().ContainSingle();
        store.Calls.Should().Equal("ResolveAnomalyAsync");
    }

    // ───────────── HTTP pipeline ─────────────

    private static async Task<PaymentHttpHost> HostAsync(Action<DispatchFake>? script = null)
    {
        var host = await PaymentHttpHost.StartAsync(paymob: true);
        host.Workflow.Handlers["ResolveAnomalyAsync"] = a =>
            new PaymentAnomalyResolveResult(PaymentAnomalyResolveOutcome.Resolved, Anomaly(PaymentAnomalyState.Resolved) with { Resolution = (string)((PaymentAnomalyResolutionRecord)a[2]!).Resolution, ResolvedBy = (Guid)a[1]!, ResolvedAtUtc = DateTime.UtcNow });
        host.Workflow.Handlers["ListAnomaliesAsync"] = _ => (IReadOnlyList<PaymentAnomaly>)[Anomaly()];
        host.Workflow.Handlers["ListTransitionsAsync"] = _ => (IReadOnlyList<FinancialTransition>)[
            new FinancialTransition(Guid.NewGuid(), DateTime.UtcNow, "PaymentAttempt", Guid.NewGuid(), Guid.NewGuid(), "Initiated", "Succeeded", "paymentattempt.initiated_to_succeeded",
                "Transition", TransitionSource.ProviderWebhook, FactBasis.ProviderVerified, "Stripe", "evt_1", null, "trace1", TransitionReason.ProviderVerdict)];
        script?.Invoke(host.Workflow);
        return host;
    }

    private const string Body = """{"investigationConfirmed":true,"resolution":"RefundedExternally","externalReference":"re_1","note":"done"}""";

    [Fact]
    public async Task AnAdministratorWithAValidToken_RecordsTheStatement()
    {
        await using var host = await HostAsync();

        using var response = await host.AdminPostAsync(Route, Body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("state").GetString().Should().Be("Resolved");
        json.GetProperty("resolution").GetString().Should().Be("RefundedExternally");
        json.GetProperty("nextAction").GetString().Should().Be("None");
        json.GetProperty("attemptStatusAtDetection").GetString().Should().Be("Initiated", "the evidence keeps the state at detection, unchanged");
    }

    [Theory]
    [InlineData("""{"investigationConfirmed":false,"resolution":"NoActionNeeded"}""", "Operations.AnomalyNotConfirmed")]
    [InlineData("""{"resolution":"NoActionNeeded"}""", "Operations.AnomalyNotConfirmed")]
    [InlineData("""{"investigationConfirmed":true}""", "Operations.AnomalyResolutionInvalid")]
    [InlineData("""{"investigationConfirmed":true,"resolution":"MarkOrderPaid"}""", "Operations.AnomalyResolutionInvalid")]
    public async Task AnUnconfirmedOrUnknownStatement_Is400_WithoutTouchingTheStore(string body, string code)
    {
        await using var host = await HostAsync();

        using var response = await host.AdminPostAsync(Route, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(code);
        host.Workflow.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PaymentAnomalyResolveOutcome.NotFound, HttpStatusCode.NotFound)]
    [InlineData(PaymentAnomalyResolveOutcome.Conflict, HttpStatusCode.Conflict)]
    public async Task ConsistentErrorStatuses(PaymentAnomalyResolveOutcome outcome, HttpStatusCode expected)
    {
        await using var host = await HostAsync(s => s.Handlers["ResolveAnomalyAsync"] = _ => new PaymentAnomalyResolveResult(outcome, null));

        using var response = await host.AdminPostAsync(Route, Body);

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task OrderProviderAndAmountsInTheBody_AreIgnored_TheAnomalyIsReadFromTheStoredRow()
    {
        await using var host = await HostAsync();
        var captured = new List<PaymentAnomalyResolutionRecord>();
        var inner = host.Workflow.Handlers["ResolveAnomalyAsync"];
        host.Workflow.Handlers["ResolveAnomalyAsync"] = a =>
        {
            captured.Add((PaymentAnomalyResolutionRecord)a[2]!);
            return inner(a);
        };

        using var response = await host.AdminPostAsync(Route,
            """{"investigationConfirmed":true,"resolution":"NoActionNeeded","orderId":"00000000-0000-0000-0000-000000000000","provider":"Paymob","amount":1,"attemptId":"00000000-0000-0000-0000-000000000000"}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        typeof(PaymentAnomalyResolutionRecord).GetProperties().Select(p => p.Name).Should().BeEquivalentTo("InvestigationConfirmed", "Resolution", "ExternalReference", "Note");
        captured.Should().ContainSingle();
    }

    [Fact]
    public async Task TheListAndHistoryEndpoints_AreAdminOnly_ValidateFilters_AndExposeNoSecrets()
    {
        await using var host = await HostAsync();

        async Task<HttpResponseMessage> Get(string path, bool admin = true, bool signedIn = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (signedIn)
            {
                request.Headers.Add("X-Test-User", Guid.NewGuid().ToString());
                if (admin)
                    request.Headers.Add("X-Test-Admin", "1");
            }

            return await host.Client.SendAsync(request);
        }

        foreach (var path in new[] { "/api/admin/payments/anomalies", "/api/admin/payments/history" })
        {
            (await Get(path, signedIn: false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await Get(path, admin: false)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await Get("/api/admin/payments/anomalies?state=Bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Get("/api/admin/payments/history?entityType=Bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var anomalies = await (await Get("/api/admin/payments/anomalies?state=Open&limit=5")).Content.ReadAsStringAsync();
        anomalies.Should().Contain("\"nextAction\":\"InvestigateThenRecord\"").And.Contain("\"receivedAmountMinor\":1").And.Contain("\"expectedAmountMinor\":9100");
        var history = await (await Get($"/api/admin/payments/history?orderId={Guid.NewGuid()}&entityType=PaymentAttempt")).Content.ReadAsStringAsync();
        history.Should().Contain("\"factBasis\":\"ProviderVerified\"").And.Contain("\"source\":\"ProviderWebhook\"").And.Contain("\"eventCode\":\"paymentattempt.initiated_to_succeeded\"");
        foreach (var body in new[] { anomalies, history })
            body.ToLowerInvariant().Should().NotContain("signature").And.NotContain("secret").And.NotContain("payload").And.NotContain("hmac").And.NotContain("customer");
    }

    // ───────────── operational visibility ─────────────

    [Fact]
    public async Task TheOverview_CarriesTheDurableUnresolvedSignals_ForAdministratorsOnly()
    {
        await using var host = await HostAsync(s => s.Handlers["GetSummaryAsync"] = _ =>
            new WorkflowSummary(0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, int>())
            {
                PaymentExceptionsOpen = 1,
                PaymentAnomaliesOpen = 3,
                PaymentAnomaliesOpenByKind = new Dictionary<string, int> { [PaymentAnomalyKind.AmountOrCurrencyMismatch] = 2, [PaymentAnomalyKind.UnknownPaymentReference] = 1 },
                OldestOpenPaymentAnomalyAgeSeconds = 7200,
                OldestOpenPaymentExceptionAgeSeconds = 60
            });

        async Task<HttpResponseMessage> Get(bool admin, bool signedIn = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments/overview");
            if (signedIn)
            {
                request.Headers.Add("X-Test-User", Guid.NewGuid().ToString());
                if (admin)
                    request.Headers.Add("X-Test-Admin", "1");
            }

            return await host.Client.SendAsync(request);
        }

        (await Get(admin: false, signedIn: false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Get(admin: false)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var ok = await Get(admin: true);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ok.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("paymentAnomaliesOpen").GetInt32().Should().Be(3);
        json.GetProperty("paymentExceptionsOpen").GetInt32().Should().Be(1);
        json.GetProperty("oldestOpenPaymentAnomalyAgeSeconds").GetInt64().Should().Be(7200);
        json.GetProperty("oldestOpenPaymentExceptionAgeSeconds").GetInt64().Should().Be(60);
        json.GetProperty("paymentAnomaliesOpenByKind").GetProperty("AmountOrCurrencyMismatch").GetInt32().Should().Be(2);
        json.GetRawText().ToLowerInvariant().Should().NotContain("signature").And.NotContain("secret").And.NotContain("customer");
    }

    [Fact]
    public async Task AnAnomalyInTheList_ShowsItsCategoryAgeAndStatus_WithoutSecretsOrCustomerData()
    {
        await using var host = await HostAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments/anomalies?state=Open");
        request.Headers.Add("X-Test-User", Guid.NewGuid().ToString());
        request.Headers.Add("X-Test-Admin", "1");

        using var response = await host.Client.SendAsync(request);
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single();

        item.GetProperty("kind").GetString().Should().Be(PaymentAnomalyKind.AmountOrCurrencyMismatch);
        item.GetProperty("state").GetString().Should().Be("Open");
        item.GetProperty("ageSeconds").GetInt64().Should().Be(180, "the age is computed on the server's clock, not the browser's");
        item.GetProperty("nextAction").GetString().Should().Be("InvestigateThenRecord");
        item.GetRawText().ToLowerInvariant().Should().NotContain("signature").And.NotContain("payload").And.NotContain("email").And.NotContain("customer");
    }
}
