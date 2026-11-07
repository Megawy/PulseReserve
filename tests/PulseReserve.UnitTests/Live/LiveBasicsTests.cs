using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PulseReserve.AppHost.Live;
using Xunit;

namespace PulseReserve.UnitTests.Live;

public sealed class LiveSignalParserTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    [Fact]
    public void AReservationNotification_BecomesOneReservationSignal()
    {
        LiveSignalParser.Parse($$"""{"k":"res","id":"{{A}}"}""").Should().Equal(LiveSignal.ForReservation(A));
    }

    [Fact]
    public void ATicketNotification_BecomesAnOrderSignal()
    {
        LiveSignalParser.Parse($$"""{"k":"tkt","o":"{{B}}"}""").Should().Equal(LiveSignal.ForOrder(B));
    }

    [Theory]
    [InlineData("Order")]
    [InlineData("PaymentAttempt")]
    public void AnOrderOrAttemptTransition_TellsTheCustomerAndTheOrdersAreaAndTheOverview(string entityType)
    {
        var signals = LiveSignalParser.Parse($$"""{"k":"fin","t":"{{entityType}}","o":"{{B}}"}""");

        signals.Should().Contain(LiveSignal.ForOrder(B)).And.Contain(LiveSignal.ForAdmin(LiveAreas.Orders)).And.Contain(LiveSignal.ForAdmin(LiveAreas.Overview));
    }

    [Fact]
    public void ARefundTransition_ReachesAdministratorsOnly_BecauseCustomersSeeNoRefundRecord()
    {
        var signals = LiveSignalParser.Parse($$"""{"k":"fin","t":"Refund","o":"{{B}}"}""");

        signals.Should().BeEquivalentTo([LiveSignal.ForAdmin(LiveAreas.Refunds), LiveSignal.ForAdmin(LiveAreas.Overview)]);
        signals.Should().NotContain(s => s.Kind == LiveSignalKind.Order);
    }

    [Fact]
    public void AnException_TellsTheCustomerTheirOrderChangedAndAdministratorsTheExceptionsArea()
    {
        var signals = LiveSignalParser.Parse($$"""{"k":"fin","t":"PaymentException","o":"{{B}}"}""");

        signals.Should().Contain(LiveSignal.ForOrder(B)).And.Contain(LiveSignal.ForAdmin(LiveAreas.Exceptions));
    }

    [Fact]
    public void AnAnomaly_ReachesAdministratorsOnly_EvenWhenItHasNoOrder()
    {
        var signals = LiveSignalParser.Parse("""{"k":"fin","t":"PaymentAnomaly","o":null}""");

        signals.Should().BeEquivalentTo([LiveSignal.ForAdmin(LiveAreas.Anomalies), LiveSignal.ForAdmin(LiveAreas.Overview)]);
    }

    [Theory]
    [InlineData("reconciliation_tasks", LiveAreas.Tasks)]
    [InlineData("outbox_events", LiveAreas.Outbox)]
    public void AWorkQueueNotification_NamesItsAreaAndTheOverview(string table, string area)
    {
        LiveSignalParser.Parse($$"""{"k":"wrk","t":"{{table}}"}""").Should().BeEquivalentTo([LiveSignal.ForAdmin(area), LiveSignal.ForAdmin(LiveAreas.Overview)]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"k":7}""")]
    [InlineData("""{"k":"unknown","id":"aaaaaaaa-0000-4000-8000-000000000001"}""")]
    [InlineData("""{"k":"res"}""")]
    [InlineData("""{"k":"res","id":"not-a-guid"}""")]
    [InlineData("""{"k":"res","id":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"k":"fin","t":"SomethingElse","o":"aaaaaaaa-0000-4000-8000-000000000001"}""")]
    [InlineData("""{"k":"wrk","t":"orders"}""")]
    public void APayloadThatIsNotUnderstood_YieldsNothing_AndNeverThrows(string? payload)
    {
        var act = () => LiveSignalParser.Parse(payload);

        act.Should().NotThrow();
        LiveSignalParser.Parse(payload).Should().BeEmpty();
    }

    [Fact]
    public void AnOversizedPayload_IsIgnored()
    {
        LiveSignalParser.Parse("{\"k\":\"res\",\"id\":\"" + new string('x', LiveSignalParser.MaxPayloadLength) + "\"}").Should().BeEmpty();
    }

    [Fact]
    public void EveryAreaTheParserCanName_IsAKnownArea()
    {
        var named = new[] { """{"k":"wrk","t":"reconciliation_tasks"}""", """{"k":"wrk","t":"outbox_events"}""", $$"""{"k":"fin","t":"Order","o":"{{B}}"}""",
            $$"""{"k":"fin","t":"Refund","o":"{{B}}"}""", $$"""{"k":"fin","t":"PaymentException","o":"{{B}}"}""", """{"k":"fin","t":"PaymentAnomaly"}""" }
            .SelectMany(p => LiveSignalParser.Parse(p)).Where(s => s.Kind == LiveSignalKind.Admin).Select(s => s.Area!);

        named.Should().OnlyContain(a => LiveAreas.All.Contains(a));
    }
}

public sealed class LiveOptionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value)).Build();

    [Fact]
    public void TheDefaults_AreSafeAndEverythingIsOn()
    {
        var options = LiveOptions.Resolve(Config());

        options.Enabled.Should().BeTrue();
        options.ChangeFeedEnabled.Should().BeTrue();
        options.HoldExpiryEnabled.Should().BeTrue();
        options.HoldExpiryInterval.Should().Be(TimeSpan.FromSeconds(5));
        options.HoldExpiryBatch.Should().Be(100);
        options.MaxConnectionAge.Should().Be(TimeSpan.FromMinutes(10));
        options.MaxEventSubscriptions.Should().Be(5);
        options.Coalesce.Should().Be(TimeSpan.FromMilliseconds(150));
        options.ChannelCapacity.Should().Be(10_000);
        options.ConnectsPerMinute.Should().Be(60);
        options.AllowedOrigins.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Live:Enabled", "yes")]
    [InlineData("Live:ChangeFeed:Enabled", "1")]
    [InlineData("Reservation:HoldExpiry:Enabled", "ture")]
    public void ATypoInAFlag_FailsStartup_InsteadOfBeingReadAsOnOrOff(string key, string value)
    {
        var act = () => LiveOptions.Resolve(Config((key, value)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*'true' or 'false'*");
    }

    [Theory]
    [InlineData("Reservation:HoldExpiry:IntervalSeconds", "0")]
    [InlineData("Reservation:HoldExpiry:IntervalSeconds", "301")]
    [InlineData("Reservation:HoldExpiry:BatchSize", "0")]
    [InlineData("Reservation:HoldExpiry:BatchSize", "1001")]
    [InlineData("Live:MaxConnectionMinutes", "0")]
    [InlineData("Live:MaxConnectionMinutes", "61")]
    [InlineData("Live:MaxEventSubscriptionsPerConnection", "0")]
    [InlineData("Live:MaxEventSubscriptionsPerConnection", "21")]
    [InlineData("Live:CoalesceMilliseconds", "9")]
    [InlineData("Live:ChannelCapacity", "99")]
    [InlineData("Live:ConnectsPerMinute", "0")]
    [InlineData("Live:ConnectsPerMinute", "10001")]
    [InlineData("Live:ConnectsPerMinute", "abc")]
    [InlineData("Live:ConnectsPerMinute", "-5")]
    public void AValueOutOfRange_FailsStartup_NamingTheSetting(string key, string value)
    {
        var act = () => LiveOptions.Resolve(Config((key, value)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }

    [Fact]
    public void AllowedOrigins_AreNormalised_AndOnlyRealOriginsAreAccepted()
    {
        LiveOptions.Resolve(Config(("Live:AllowedOrigins", "https://App.Example.com , http://localhost:4200;https://other.example")))
            .AllowedOrigins.Should().Equal("https://app.example.com", "http://localhost:4200", "https://other.example");

        foreach (var bad in new[] { "app.example.com", "https://app.example.com/path", "https://user@app.example.com", "ftp://app.example.com", "https://app.example.com?x=1", "*" })
        {
            var act = () => LiveOptions.Resolve(Config(("Live:AllowedOrigins", bad)));
            act.Should().Throw<InvalidOperationException>().WithMessage($"*Live:AllowedOrigins*{bad}*");
        }
    }
}

public sealed class LiveOriginTests
{
    private static HttpRequest Request(string host, string? origin, string scheme = "http")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = HostString.FromUriComponent(host);
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        return context.Request;
    }

    private static LiveOptions Options(string? allowed = null) =>
        LiveOptions.Resolve(new ConfigurationBuilder().AddInMemoryCollection(allowed is null ? [] : new Dictionary<string, string?> { ["Live:AllowedOrigins"] = allowed }).Build());

    [Theory]
    [InlineData("app.example.com", null)]                       // a non-browser client sends no Origin
    [InlineData("app.example.com", "https://app.example.com")]  // same host, default https port
    [InlineData("app.example.com", "http://app.example.com")]   // same host, default http port (scheme is not compared)
    [InlineData("localhost:4200", "http://localhost:4200")]
    [InlineData("APP.example.com", "https://app.example.com")]  // host names are case-insensitive
    public void TheSameOriginAndAClientWithoutOrigin_AreAllowed(string host, string? origin)
    {
        LiveOrigin.IsAllowed(Request(host, origin), Options()).Should().BeTrue();
    }

    [Theory]
    [InlineData("app.example.com", "https://evil.example")]
    [InlineData("app.example.com", "https://app.example.com.evil.example")]
    [InlineData("app.example.com", "https://evilapp.example.com")]
    [InlineData("localhost:4200", "http://localhost:4300")]     // another port is another origin
    [InlineData("localhost:4200", "http://localhost")]
    [InlineData("app.example.com", "null")]                     // an opaque origin (sandboxed iframe, file://)
    [InlineData("app.example.com", "not a url")]
    [InlineData("app.example.com", "ftp://app.example.com")]
    public void AnotherOriginAndAnOpaqueOrMalformedOrigin_AreRefused(string host, string origin)
    {
        LiveOrigin.IsAllowed(Request(host, origin), Options()).Should().BeFalse();
    }

    [Fact]
    public void AListedOrigin_IsAllowedEvenWhenTheHostDiffers()
    {
        LiveOrigin.IsAllowed(Request("api.example.com", "https://app.example.com"), Options("https://app.example.com")).Should().BeTrue();
        LiveOrigin.IsAllowed(Request("api.example.com", "https://other.example.com"), Options("https://app.example.com")).Should().BeFalse();
    }
}
