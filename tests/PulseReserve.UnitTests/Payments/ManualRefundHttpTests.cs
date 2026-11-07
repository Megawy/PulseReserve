using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// The manual-completion route through the real HTTP pipeline: authorization, antiforgery and body binding. The store is
/// scripted, so this proves nothing about SQL, locking or constraints (those are in the PostgreSQL integration tests).
/// </summary>
public sealed class ManualRefundHttpTests
{
    private static readonly Guid RefundId = Guid.NewGuid();
    private const string Path = "/api/admin/payments/refunds/eeeeeeee-0000-4000-8000-000000000001/manual-completion";
    private const string Body = """{"confirmedPerformedExternally":true,"amount":91,"currency":"USD","externalReference":"PM-1","note":"done"}""";

    private static Refund Stored(string provider = "Paymob", string state = RefundState.NeedsReview) =>
        new(Guid.Parse("eeeeeeee-0000-4000-8000-000000000001"), Guid.NewGuid(), Guid.NewGuid(), 91m, "USD", "PaymentAfterSaleFailed", state, provider, false, 1, null, 1, null, null,
            DateTime.UtcNow, null, DateTime.UtcNow, DateTime.UtcNow);

    private static async Task<(PaymentHttpHost Host, List<ManualRefundRecord> Recorded)> StartAsync(Refund? stored = null)
    {
        var host = await PaymentHttpHost.StartAsync(paymob: true);
        var refund = stored ?? Stored();
        var recorded = new List<ManualRefundRecord>();
        host.Workflow.Handlers["GetRefundAsync"] = _ => refund;
        host.Workflow.Handlers["RecordManualRefundAsync"] = a =>
        {
            recorded.Add((ManualRefundRecord)a[2]!);
            return new ManualRefundResult(ManualRefundOutcome.Recorded, refund with { State = RefundState.ManuallyRecorded, ManualReference = "PM-1", ManuallyRecordedBy = (Guid)a[1]!, ManuallyRecordedAtUtc = DateTime.UtcNow });
        };
        return (host, recorded);
    }

    [Fact]
    public async Task AnAdministratorWithAValidToken_RecordsIt_AndTheAnswerIsManuallyRecorded()
    {
        var (host, recorded) = await StartAsync();
        await using var _ = host;

        using var response = await host.AdminPostAsync(Path, Body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("state").GetString().Should().Be("ManuallyRecorded");
        json.GetProperty("manualReference").GetString().Should().Be("PM-1");
        json.GetProperty("manuallyRecordedBy").GetGuid().Should().NotBeEmpty();
        recorded.Should().ContainSingle().Which.ConfirmedPerformedExternally.Should().BeTrue();
    }

    [Fact]
    public async Task WithoutSigningIn_Is401()
    {
        var (host, recorded) = await StartAsync();
        await using var _ = host;

        using var response = await host.AdminPostAsync(Path, Body, signedIn: false, csrf: false);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task ACustomer_Is403()
    {
        var (host, recorded) = await StartAsync();
        await using var _ = host;

        using var response = await host.AdminPostAsync(Path, Body, admin: false);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAdministratorWithoutTheAntiforgeryToken_IsRefused_AndNothingIsRecorded()
    {
        var (host, recorded) = await StartAsync();
        await using var _ = host;

        using var response = await host.AdminPostAsync(Path, Body, csrf: false);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        recorded.Should().BeEmpty();
        host.Workflow.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"confirmedPerformedExternally":false,"amount":91,"currency":"USD"}""", "Operations.ManualRefundNotConfirmed")]
    [InlineData("""{"amount":91,"currency":"USD"}""", "Operations.ManualRefundNotConfirmed")]
    [InlineData("""{"confirmedPerformedExternally":true,"currency":"USD"}""", "Operations.ManualRefundInvalid")]
    [InlineData("""{"confirmedPerformedExternally":true,"amount":0,"currency":"USD"}""", "Operations.ManualRefundInvalid")]
    [InlineData("""{"confirmedPerformedExternally":true,"amount":91,"currency":"usd"}""", "Operations.ManualRefundInvalid")]
    public async Task AnUnconfirmedOrInvalidBody_IsA400_WithoutTouchingTheStore(string body, string code)
    {
        var (host, recorded) = await StartAsync();
        await using var _ = host;

        using var response = await host.AdminPostAsync(Path, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(code);
        recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task AStripeRefund_Is409_ItKeepsItsAutomaticPath()
    {
        var (host, recorded) = await StartAsync(Stored("Stripe", RefundState.Failed));
        await using var _ = host;

        using var response = await host.AdminPostAsync(Path, Body);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task ProviderAndOrderInTheBodyAreIgnored_TheyComeFromTheStoredRefund()
    {
        var (host, recorded) = await StartAsync();
        await using var _ = host;
        var body = """{"confirmedPerformedExternally":true,"amount":91,"currency":"USD","provider":"Stripe","orderId":"00000000-0000-0000-0000-000000000000"}""";

        using var response = await host.AdminPostAsync(Path, body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        typeof(ManualRefundRecord).GetProperties().Select(p => p.Name).Should().NotContain(["Provider", "OrderId"]);
        recorded.Should().ContainSingle();
    }

    [Fact]
    public async Task MalformedJson_Is400_AndNeverRecords()
    {
        var (host, recorded) = await StartAsync();
        await using var _ = host;

        using var response = await host.AdminPostAsync(Path, "{not json");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnsupportedMediaType);
        recorded.Should().BeEmpty();
    }
}
