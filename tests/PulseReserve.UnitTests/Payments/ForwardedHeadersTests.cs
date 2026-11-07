using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PulseReserve.AppHost.Security;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// Forwarded-header trust and the address-based sign-in limit. The pipeline under test uses the real extension methods
/// (<c>UsePulseReserveForwardedHeaders</c>, <c>AddAuthRateLimit</c>, <c>ClientAddressKey</c>) in the order Program.cs uses them
/// (forwarded headers first, then the rate limiter); the peer address of each request is chosen by the test.
/// </summary>
public sealed class ForwardedHeadersTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    // ───────────── configuration: fail closed, never guess ─────────────

    [Fact]
    public void InDevelopmentAndTesting_TheModeDefaultsToDirect() =>
        ForwardedHeadersSettings.Resolve(Config(), relaxedEnvironment: true).BehindProxy.Should().BeFalse();

    [Fact]
    public void OutsideDevelopmentAndTesting_TheModeMustBeChosenExplicitly() =>
        ((Action)(() => ForwardedHeadersSettings.Resolve(Config(), relaxedEnvironment: false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*Security:ForwardedHeaders:Mode*required*");

    [Fact]
    public void Direct_IsAcceptedInProduction_AndIgnoresForwardedHeaders() =>
        ForwardedHeadersSettings.Resolve(Config((ForwardedHeadersSettings.ModeKey, "direct")), relaxedEnvironment: false).BehindProxy.Should().BeFalse();

    [Fact]
    public void Direct_WithListedProxies_IsContradictory_AndFailsStartup() =>
        ((Action)(() => ForwardedHeadersSettings.Resolve(Config((ForwardedHeadersSettings.ModeKey, "Direct"), ("Security:ForwardedHeaders:KnownProxies", "10.0.0.5")), false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*Direct*");

    [Theory]
    [InlineData("Proxy", null, null, "*no Security:ForwardedHeaders:KnownProxies or KnownNetworks*")]
    [InlineData("Proxy", "not-an-ip", null, "*not an IP address*")]
    [InlineData("Proxy", null, "10.0.0.0", "*not a CIDR*")]
    [InlineData("Proxy", null, "0.0.0.0/0", "*catch-all*")]
    [InlineData("Proxy", null, "::/0", "*catch-all*")]
    [InlineData("Gateway", "10.0.0.5", null, "*must be 'Direct' or 'Proxy'*")]
    public void AnUnsafeOrIncompleteProxyConfiguration_FailsStartup(string mode, string? proxies, string? networks, string message) =>
        ((Action)(() => ForwardedHeadersSettings.Resolve(Config(
            (ForwardedHeadersSettings.ModeKey, mode), ("Security:ForwardedHeaders:KnownProxies", proxies), ("Security:ForwardedHeaders:KnownNetworks", networks)), false)))
            .Should().Throw<InvalidOperationException>().WithMessage(message);

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("-1")]
    [InlineData("many")]
    public void TheForwardLimit_MustBeSmall(string limit) =>
        ((Action)(() => ForwardedHeadersSettings.Resolve(Config(
            (ForwardedHeadersSettings.ModeKey, "Proxy"), ("Security:ForwardedHeaders:KnownProxies", "10.0.0.5"), ("Security:ForwardedHeaders:ForwardLimit", limit)), false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*ForwardLimit*");

    [Fact]
    public void AValidProxyConfiguration_IsParsed()
    {
        var settings = ForwardedHeadersSettings.Resolve(Config(
            (ForwardedHeadersSettings.ModeKey, "Proxy"), ("Security:ForwardedHeaders:KnownProxies", "10.0.0.5; 2001:db8::1"),
            ("Security:ForwardedHeaders:KnownNetworks", "192.168.0.0/16")), false);

        settings.BehindProxy.Should().BeTrue();
        settings.KnownProxies.Should().HaveCount(2);
        settings.KnownNetworks.Should().ContainSingle();
        settings.ForwardLimit.Should().Be(1);
    }

    // ───────────── behaviour through the real middleware ─────────────

    private sealed record Seen(string Address, string Scheme, string Key);

    private static async Task<WebApplication> StartAsync(ForwardedHeadersSettings settings, int authPermits = 2)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddAuthRateLimit(authPermits);
        });
        var app = builder.Build();
        // The peer address is whatever the test says (a real server supplies the socket's address).
        app.Use((context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Peer", out var peer))
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
            return next();
        });
        app.UsePulseReserveForwardedHeaders(settings); // the order Program.cs uses: forwarded headers, then the limiter
        app.UseRateLimiter();
        app.MapGet("/whoami", (HttpContext http) => Results.Ok(new Seen(http.Connection.RemoteIpAddress?.ToString() ?? "none", http.Request.Scheme, SecurityExtensions.ClientAddressKey(http))));
        app.MapPost("/signin", () => Results.Ok()).RequireRateLimiting(AuthPolicies.AuthRateLimit);
        await app.StartAsync();
        return app;
    }

    private static HttpClient ClientOf(WebApplication app) => ((TestServer)app.Services.GetRequiredService<IServer>()).CreateClient();

    private static async Task<Seen> WhoAmI(HttpClient client, string peer, string? forwardedFor = null, string? proto = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        request.Headers.Add("X-Test-Peer", peer);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (proto is not null)
            request.Headers.Add("X-Forwarded-Proto", proto);
        using var response = await client.SendAsync(request);
        return (await response.Content.ReadFromJsonAsync<Seen>())!;
    }

    private static async Task<HttpStatusCode> SignIn(HttpClient client, string peer, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/signin");
        request.Headers.Add("X-Test-Peer", peer);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static ForwardedHeadersSettings Proxy(string proxies, string? networks = null, int limit = 1) =>
        ForwardedHeadersSettings.Resolve(Config(
            (ForwardedHeadersSettings.ModeKey, "Proxy"), ("Security:ForwardedHeaders:KnownProxies", proxies),
            ("Security:ForwardedHeaders:KnownNetworks", networks), ("Security:ForwardedHeaders:ForwardLimit", limit.ToString())), false);

    [Fact]
    public async Task ATrustedProxy_SuppliesTheRealClientAddressAndScheme()
    {
        await using var app = await StartAsync(Proxy("10.0.0.5"));

        var seen = await WhoAmI(ClientOf(app), peer: "10.0.0.5", forwardedFor: "203.0.113.9", proto: "https");

        seen.Should().Be(new Seen("203.0.113.9", "https", "203.0.113.9"));
    }

    [Fact]
    public async Task ANetworkListedAsTrusted_IsTrustedToo()
    {
        await using var app = await StartAsync(Proxy("10.0.0.5", networks: "172.16.0.0/12"));

        (await WhoAmI(ClientOf(app), peer: "172.20.1.1", forwardedFor: "203.0.113.9")).Address.Should().Be("203.0.113.9");
    }

    [Fact]
    public async Task AnUntrustedPeer_CannotChooseItsOwnAddressOrScheme()
    {
        await using var app = await StartAsync(Proxy("10.0.0.5"));

        var seen = await WhoAmI(ClientOf(app), peer: "198.51.100.7", forwardedFor: "203.0.113.9", proto: "https");

        seen.Should().Be(new Seen("198.51.100.7", "http", "198.51.100.7"), "the headers of a peer that is not a listed proxy are ignored");
    }

    [Fact]
    public async Task LoopbackIsNotTrustedImplicitly_WhenOnlyOtherProxiesAreListed()
    {
        await using var app = await StartAsync(Proxy("10.0.0.5"));

        (await WhoAmI(ClientOf(app), peer: "127.0.0.1", forwardedFor: "203.0.113.9")).Address.Should().Be("127.0.0.1");
    }

    [Fact]
    public async Task InDirectMode_ForwardedHeadersAreIgnored_EvenFromLoopback()
    {
        await using var app = await StartAsync(ForwardedHeadersSettings.Direct);

        (await WhoAmI(ClientOf(app), peer: "127.0.0.1", forwardedFor: "203.0.113.9", proto: "https")).Should().Be(new Seen("127.0.0.1", "http", "127.0.0.1"));
    }

    [Fact]
    public async Task WithTheDefaultLimit_OnlyTheNearestHopIsBelieved_NotTheClientsOwnLeadingValue()
    {
        await using var app = await StartAsync(Proxy("10.0.0.5"));

        // The client sent "198.51.100.1" itself; the trusted proxy appended the address it actually saw.
        (await WhoAmI(ClientOf(app), peer: "10.0.0.5", forwardedFor: "198.51.100.1, 203.0.113.9")).Address.Should().Be("203.0.113.9");
    }

    [Fact]
    public async Task TheSignInLimit_IsPerRealClient_BehindATrustedProxy()
    {
        await using var app = await StartAsync(Proxy("10.0.0.5"), authPermits: 2);
        var client = ClientOf(app);

        (await SignIn(client, "10.0.0.5", "203.0.113.1")).Should().Be(HttpStatusCode.OK);
        (await SignIn(client, "10.0.0.5", "203.0.113.1")).Should().Be(HttpStatusCode.OK);
        (await SignIn(client, "10.0.0.5", "203.0.113.1")).Should().Be(HttpStatusCode.TooManyRequests, "the same client is limited");
        (await SignIn(client, "10.0.0.5", "203.0.113.2")).Should().Be(HttpStatusCode.OK, "another client behind the same proxy has its own allowance");
    }

    [Fact]
    public async Task AnAttackerRotatingForwardedForValues_StaysInTheSingleBucketOfItsRealAddress()
    {
        await using var app = await StartAsync(Proxy("10.0.0.5"), authPermits: 2);
        var client = ClientOf(app);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            codes.Add(await SignIn(client, "198.51.100.7", $"203.0.113.{i + 10}")); // the peer is not a listed proxy

        codes.Take(2).Should().OnlyContain(c => c == HttpStatusCode.OK);
        codes.Skip(2).Should().OnlyContain(c => c == HttpStatusCode.TooManyRequests, "spoofed headers do not create fresh allowances");
    }

    [Fact]
    public async Task WithoutAnyProxy_TheLimitIsPerPeer_AsBefore()
    {
        await using var app = await StartAsync(ForwardedHeadersSettings.Direct, authPermits: 1);
        var client = ClientOf(app);

        (await SignIn(client, "198.51.100.7")).Should().Be(HttpStatusCode.OK);
        (await SignIn(client, "198.51.100.7")).Should().Be(HttpStatusCode.TooManyRequests);
        (await SignIn(client, "198.51.100.8")).Should().Be(HttpStatusCode.OK);
    }
}
