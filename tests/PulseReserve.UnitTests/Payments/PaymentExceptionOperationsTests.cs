using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// The operator side of a payment exception (a verified success for an attempt recorded as Failed): service rules, the HTTP
/// pipeline (authorization, antiforgery, binding) and what the API exposes. The store is scripted, so none of this proves SQL,
/// locking or constraints; those are in the PostgreSQL integration tests, which need Docker and were not run.
/// </summary>
public sealed class PaymentExceptionOperationsTests
{
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid ExceptionId = Guid.Parse("cccccccc-0000-4000-8000-000000000001");
    private static readonly string Route = $"/api/admin/payments/exceptions/{ExceptionId}/resolve";

    private static PaymentException Exception(string state = PaymentExceptionState.Open, string orderStatus = OrderStatus.PendingPayment, string? refundState = null) => new(
        ExceptionId, PaymentExceptionKind.LateSuccessAfterFailedAttempt, Guid.NewGuid(), Guid.NewGuid(), "Stripe", false, 91m, "USD",
        PaymentAttemptStatus.Failed, "checkout_session_expired", DateTime.UtcNow.AddMinutes(-20), OrderStatus.PendingPayment,
        DateTime.UtcNow.AddMinutes(-5), state, null, null, null, null, null, orderStatus, DateTime.UtcNow.AddMinutes(10), refundState);

    private static PaymentExceptionResolutionRecord Valid(bool confirmed = true) => new(confirmed, 91m, "USD", "re_dashboard_1", "refunded in the dashboard");

    private static (PaymentOperationsService Service, DispatchFake Store, List<PaymentExceptionResolutionRecord> Recorded) Build(PaymentExceptionResolveOutcome outcome)
    {
        var store = DispatchFake.Create<IWorkflowStore>(out var fake);
        var recorded = new List<PaymentExceptionResolutionRecord>();
        fake.Handlers["ResolveExceptionAsync"] = a =>
        {
            recorded.Add((PaymentExceptionResolutionRecord)a[2]!);
            return new PaymentExceptionResolveResult(outcome, outcome is PaymentExceptionResolveOutcome.NotFound or PaymentExceptionResolveOutcome.AmountMismatch ? null : Exception(PaymentExceptionState.Resolved));
        };
        return (new PaymentOperationsService(store, new PaymentProviderRegistry([new ScriptedProvider("Stripe", false, "USD")])), fake, recorded);
    }

    // ───────────── what an exception says ─────────────

    [Fact]
    public void TheOnlySupportedNextStep_IsDerivedFromTheState_NeverFromTheClient()
    {
        Exception().NextAction.Should().Be(PaymentExceptionAction.RefundExternallyThenRecord);
        Exception(PaymentExceptionState.Resolved).NextAction.Should().Be(PaymentExceptionAction.None);
    }

    [Fact]
    public void AnExceptionCarriesNoPayloadSignatureSecretReferenceOrCustomerIdentifier()
    {
        var names = typeof(PaymentException).GetProperties().Select(p => p.Name).ToArray();

        names.Should().NotContain(n => n.Contains("Signature") || n.Contains("Secret") || n.Contains("Payload") || n.Contains("Customer")
                                         || n.Contains("ProviderReference") || n.Contains("CheckoutUrl") || n.Contains("ProviderEventId"));
        names.Should().Contain(["OriginalAttemptStatus", "OriginalVerdictAtUtc", "DetectedAtUtc", "OrderStatus", "OrderRefundState", "NextAction"]);
    }

    // ───────────── resolution rules ─────────────

    [Fact]
    public async Task AResolutionNeedsTheExplicitConfirmation_AndTouchesNothingWithoutIt()
    {
        var (service, store, recorded) = Build(PaymentExceptionResolveOutcome.Resolved);

        var result = await service.ResolveExceptionAsync(ExceptionId, Valid(confirmed: false), Admin);

        result.Error.Should().Be(PaymentsErrors.ExceptionNotConfirmed);
        store.Calls.Should().BeEmpty();
        recorded.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, "USD")]
    [InlineData(-1, "USD")]
    [InlineData(91, "usd")]
    [InlineData(91, "US")]
    [InlineData(91, "")]
    public async Task AnInvalidAmountOrCurrency_IsRejectedBeforeTheStore(int amount, string currency)
    {
        var (service, store, _) = Build(PaymentExceptionResolveOutcome.Resolved);

        var result = await service.ResolveExceptionAsync(ExceptionId, Valid() with { Amount = amount, Currency = currency }, Admin);

        result.Error.Should().Be(PaymentsErrors.ExceptionResolutionInvalid);
        store.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task OverlongText_IsRejected()
    {
        var (service, _, _) = Build(PaymentExceptionResolveOutcome.Resolved);

        (await service.ResolveExceptionAsync(ExceptionId, Valid() with { ExternalReference = new string('x', 201) }, Admin)).Error.Should().Be(PaymentsErrors.ExceptionResolutionInvalid);
        (await service.ResolveExceptionAsync(ExceptionId, Valid() with { Note = new string('x', 301) }, Admin)).Error.Should().Be(PaymentsErrors.ExceptionResolutionInvalid);
    }

    [Theory]
    [InlineData(PaymentExceptionResolveOutcome.NotFound, "Operations.NotFound")]
    [InlineData(PaymentExceptionResolveOutcome.AmountMismatch, "Operations.ExceptionAmountMismatch")]
    [InlineData(PaymentExceptionResolveOutcome.Conflict, "Operations.ExceptionResolutionConflict")]
    public async Task TheStoresVerdictUnderTheRowLock_IsReportedAsFailure(PaymentExceptionResolveOutcome outcome, string code)
    {
        var (service, _, _) = Build(outcome);

        var result = await service.ResolveExceptionAsync(ExceptionId, Valid(), Admin);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(code);
    }

    [Theory]
    [InlineData(PaymentExceptionResolveOutcome.Resolved)]
    [InlineData(PaymentExceptionResolveOutcome.Replay)]
    public async Task ARecordedOrReplayedResolution_IsASuccess_ThatIsStillAnAdministratorStatement(PaymentExceptionResolveOutcome outcome)
    {
        var (service, store, recorded) = Build(outcome);

        var result = await service.ResolveExceptionAsync(ExceptionId, Valid(), Admin);

        result.IsSuccess.Should().BeTrue();
        result.Value.State.Should().Be(PaymentExceptionState.Resolved);
        recorded.Should().ContainSingle();
        store.Calls.Should().Equal("ResolveExceptionAsync");
    }

    // ───────────── HTTP pipeline ─────────────

    private static async Task<PaymentHttpHost> HostAsync(Action<DispatchFake>? script = null)
    {
        var host = await PaymentHttpHost.StartAsync(paymob: true);
        host.Workflow.Handlers["ResolveExceptionAsync"] = a =>
            new PaymentExceptionResolveResult(PaymentExceptionResolveOutcome.Resolved, Exception(PaymentExceptionState.Resolved) with { Resolution = PaymentExceptionResolution.RefundedExternally, ResolvedBy = (Guid)a[1]!, ResolvedAtUtc = DateTime.UtcNow, ResolutionReference = "re_dashboard_1" });
        host.Workflow.Handlers["ListExceptionsAsync"] = a => (IReadOnlyList<PaymentException>)[Exception()];
        script?.Invoke(host.Workflow);
        return host;
    }

    private const string Body = """{"refundPerformedExternally":true,"amount":91,"currency":"USD","externalReference":"re_dashboard_1","note":"done"}""";

    [Fact]
    public async Task AnAdministratorWithAValidToken_ResolvesIt_AndTheAnswerIsAResolvedAdministratorStatement()
    {
        await using var host = await HostAsync();

        using var response = await host.AdminPostAsync(Route, Body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("state").GetString().Should().Be("Resolved");
        json.GetProperty("resolution").GetString().Should().Be("RefundedExternally");
        json.GetProperty("resolvedBy").GetGuid().Should().NotBeEmpty();
        json.GetProperty("nextAction").GetString().Should().Be("None");
        json.GetProperty("originalAttemptStatus").GetString().Should().Be("Failed", "the original verdict is still reported as it was");
    }

    [Fact]
    public async Task WithoutSigningIn_Is401_AsACustomer_403_WithoutTheToken_403_AndNothingIsResolved()
    {
        await using var host = await HostAsync();

        (await host.AdminPostAsync(Route, Body, signedIn: false, csrf: false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await host.AdminPostAsync(Route, Body, admin: false)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await host.AdminPostAsync(Route, Body, csrf: false)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.Workflow.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"refundPerformedExternally":false,"amount":91,"currency":"USD"}""", "Operations.ExceptionNotConfirmed")]
    [InlineData("""{"amount":91,"currency":"USD"}""", "Operations.ExceptionNotConfirmed")]
    [InlineData("""{"refundPerformedExternally":true,"currency":"USD"}""", "Operations.ExceptionResolutionInvalid")]
    [InlineData("""{"refundPerformedExternally":true,"amount":0,"currency":"USD"}""", "Operations.ExceptionResolutionInvalid")]
    [InlineData("""{"refundPerformedExternally":true,"amount":91,"currency":"usd"}""", "Operations.ExceptionResolutionInvalid")]
    public async Task AnUnconfirmedOrInvalidBody_Is400_WithoutTouchingTheStore(string body, string code)
    {
        await using var host = await HostAsync();

        using var response = await host.AdminPostAsync(Route, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(code);
        host.Workflow.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PaymentExceptionResolveOutcome.NotFound, HttpStatusCode.NotFound)]
    [InlineData(PaymentExceptionResolveOutcome.AmountMismatch, HttpStatusCode.Conflict)]
    [InlineData(PaymentExceptionResolveOutcome.Conflict, HttpStatusCode.Conflict)]
    public async Task ConsistentErrorStatuses(PaymentExceptionResolveOutcome outcome, HttpStatusCode expected)
    {
        await using var host = await HostAsync(s => s.Handlers["ResolveExceptionAsync"] = _ => new PaymentExceptionResolveResult(outcome, null));

        using var response = await host.AdminPostAsync(Route, Body);

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task ProviderOrderAndAmountInTheBodyAreIgnored_TheyComeFromTheStoredException()
    {
        await using var host = await HostAsync();
        var captured = new List<PaymentExceptionResolutionRecord>();
        var inner = host.Workflow.Handlers["ResolveExceptionAsync"];
        host.Workflow.Handlers["ResolveExceptionAsync"] = a => { captured.Add((PaymentExceptionResolutionRecord)a[2]!); return inner(a); };

        using var response = await host.AdminPostAsync(Route, """{"refundPerformedExternally":true,"amount":91,"currency":"USD","provider":"Paymob","orderId":"00000000-0000-0000-0000-000000000000"}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        typeof(PaymentExceptionResolutionRecord).GetProperties().Select(p => p.Name).Should().NotContain(["Provider", "OrderId", "AttemptId"]);
        captured.Should().ContainSingle();
    }

    [Fact]
    public async Task TheListEndpoint_IsAdminOnly_ValidatesTheStateFilter_AndExposesNoSecrets()
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

        (await Get("/api/admin/payments/exceptions", signedIn: false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Get("/api/admin/payments/exceptions", admin: false)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get("/api/admin/payments/exceptions?state=Bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var ok = await Get("/api/admin/payments/exceptions?state=Open&limit=5");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ok.Content.ReadAsStringAsync();
        body.Should().Contain("\"nextAction\":\"RefundExternallyThenRecord\"").And.Contain("\"originalAttemptStatus\":\"Failed\"");
        body.ToLowerInvariant().Should().NotContain("signature").And.NotContain("secret").And.NotContain("payload").And.NotContain("customer");
    }
}
