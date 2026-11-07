using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using PulseReserve.AppHost.Security;
using PulseReserve.IntegrationTests.Catalog;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

// Identity and authentication through the real API pipeline against a disposable PostgreSQL container.
public class IdentityApiHttpTests : IClassFixture<PostgreSqlDatabaseFixture>, IDisposable
{
    private readonly PostgreSqlDatabaseFixture _database;
    private readonly ControllableClock _clock = new();
    private readonly CatalogApiFactory _factory;
    private readonly HttpClient _anonymous;

    public IdentityApiHttpTests(PostgreSqlDatabaseFixture database)
    {
        _database = database;
        _factory = new CatalogApiFactory(database, null, services =>
            services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o => o.TimeProvider = _clock));
        _anonymous = _factory.CreateClient();
    }

    public void Dispose()
    {
        _anonymous.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Register_CreatesAccount_SignsIn_AndNeverExposesOrStoresThePlainPassword()
    {
        var csrf = await AuthTestSupport.RefreshCsrfAsync(_anonymous);
        var email = $"new-{Guid.NewGuid():N}@example.com";

        var response = await _anonymous.PostAsJsonAsync("/api/identity/register", new { email, password = AuthTestSupport.Password, displayName = "Ada" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(AuthTestSupport.Password).And.NotContainEquivalentOf("passwordHash");
        var session = JsonDocument.Parse(body).RootElement;
        session.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue();
        session.GetProperty("user").GetProperty("email").GetString().Should().Be(email);
        session.GetProperty("csrfToken").GetString().Should().NotBe(csrf, "the token is re-issued for the signed-in user");

        var authCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("pulsereserve.auth="));
        authCookie.Should().ContainEquivalentOf("httponly").And.ContainEquivalentOf("samesite=strict");

        await using var db = _database.CreateIdentityDbContext();
        var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        stored.PasswordHash.Should().NotContain(AuthTestSupport.Password);
        stored.PasswordHash.Should().StartWith("AQAAAA", "ASP.NET PasswordHasher v3 format (PBKDF2 with a per-user salt)");
    }

    [Theory]
    [InlineData("not-an-email", "correct-horse-battery-1", "Ada", "Identity.InvalidEmail")]
    [InlineData("", "correct-horse-battery-1", "Ada", "Identity.InvalidEmail")]
    [InlineData("a@example.com", "short", "Ada", "Identity.WeakPassword")]
    [InlineData("a@example.com", "correct-horse-battery-1", "  ", "Identity.InvalidDisplayName")]
    public async Task Register_RejectsInvalidInput_WithoutCreatingAnAccount(string email, string password, string name, string code)
    {
        await AuthTestSupport.RefreshCsrfAsync(_anonymous);
        var response = await _anonymous.PostAsJsonAsync("/api/identity/register", new { email, password, displayName = name });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString().Should().Be(code);
        response.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).Should().NotContain(c => c.StartsWith("pulsereserve.auth="));
    }

    [Fact]
    public async Task Register_RejectsDuplicateEmails_CaseInsensitively()
    {
        var user = await AuthTestSupport.RegisterAsync(_factory);
        await AuthTestSupport.RefreshCsrfAsync(_anonymous);

        var response = await _anonymous.PostAsJsonAsync("/api/identity/register",
            new { email = user.Email.ToUpperInvariant(), password = AuthTestSupport.Password, displayName = "Copy" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString().Should().Be("Identity.EmailTaken");
    }

    [Fact]
    public async Task Login_Succeeds_ForRegisteredUser_AndSessionReflectsIt_UntilLogout()
    {
        var user = await AuthTestSupport.RegisterAsync(_factory);
        var client = _factory.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(client);

        var login = await client.PostAsJsonAsync("/api/identity/login", new { email = user.Email.ToUpperInvariant(), password = AuthTestSupport.Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        AuthTestSupport.ApplyToken(client, (await login.Content.ReadFromJsonAsync<JsonElement>()));

        var session = await client.GetFromJsonAsync<JsonElement>("/api/identity/session");
        session.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue();
        session.GetProperty("user").GetProperty("id").GetGuid().Should().Be(user.Id);

        var logout = await client.PostAsync("/api/identity/logout", null);
        logout.StatusCode.Should().Be(HttpStatusCode.OK);
        AuthTestSupport.ApplyToken(client, await logout.Content.ReadFromJsonAsync<JsonElement>());
        (await client.GetFromJsonAsync<JsonElement>("/api/identity/session")).GetProperty("isAuthenticated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Login_Failures_AreIndistinguishable_ForUnknownEmailAndWrongPassword()
    {
        var user = await AuthTestSupport.RegisterAsync(_factory);
        var client = _factory.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(client);

        var wrongPassword = await client.PostAsJsonAsync("/api/identity/login", new { email = user.Email, password = "definitely-the-wrong-one" });
        var unknown = await client.PostAsJsonAsync("/api/identity/login", new { email = $"nobody-{Guid.NewGuid():N}@example.com", password = "definitely-the-wrong-one" });

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var a = await wrongPassword.Content.ReadAsStringAsync();
        var b = await unknown.Content.ReadAsStringAsync();
        JsonDocument.Parse(a).RootElement.GetProperty("title").GetString().Should().Be("Identity.InvalidCredentials");
        JsonDocument.Parse(a).RootElement.GetProperty("detail").GetString()
            .Should().Be(JsonDocument.Parse(b).RootElement.GetProperty("detail").GetString());
        wrongPassword.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).Should().NotContain(c => c.StartsWith("pulsereserve.auth="));
    }

    [Fact]
    public async Task RepeatedFailures_LockTheAccountTemporarily_EvenForTheCorrectPassword()
    {
        var user = await AuthTestSupport.RegisterAsync(_factory);
        var client = _factory.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(client);

        for (var i = 0; i < 5; i++)
            (await client.PostAsJsonAsync("/api/identity/login", new { email = user.Email, password = "wrong-password-1" }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var locked = await client.PostAsJsonAsync("/api/identity/login", new { email = user.Email, password = AuthTestSupport.Password });
        locked.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a locked account answers exactly like a wrong password");

        await using (var db = _database.CreateIdentityDbContext())
            await db.Database.ExecuteSqlAsync($"UPDATE identity.users SET lockout_end_utc = now() - interval '1 minute' WHERE id = {user.Id}");

        (await client.PostAsJsonAsync("/api/identity/login", new { email = user.Email, password = AuthTestSupport.Password }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StateChangingRequests_WithoutAValidCsrfToken_AreRejected()
    {
        var client = _factory.CreateClient(); // no session call, no token
        var login = await client.PostAsJsonAsync("/api/identity/login", new { email = "a@example.com", password = "whatever-123456" });
        login.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString().Should().Be("Csrf.Invalid");

        var user = await AuthTestSupport.RegisterAsync(_factory);
        user.Client.DefaultRequestHeaders.Remove(AuthTestSupport.CsrfHeader);
        var release = await user.Client.PostAsync($"/api/reservations/{Guid.NewGuid()}/release", null);
        release.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        user.Client.DefaultRequestHeaders.Add(AuthTestSupport.CsrfHeader, "forged-token");
        (await user.Client.PostAsync($"/api/reservations/{Guid.NewGuid()}/release", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProtectedEndpoints_Return401_WithoutASession()
    {
        await AuthTestSupport.RefreshCsrfAsync(_anonymous);
        var id = Guid.NewGuid();

        (await _anonymous.PostAsJsonAsync("/api/reservations", new { eventId = id, seatIds = new[] { id } }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _anonymous.GetAsync($"/api/reservations/{id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _anonymous.PostAsync($"/api/reservations/{id}/release", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ForgedOrTamperedSessionCookies_AreRejected()
    {
        var client = _factory.CreateClient(new() { HandleCookies = false });
        var id = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/reservations/{id}");
        request.Headers.Add("Cookie", "pulsereserve.auth=CfDJ8-not-a-real-protected-ticket");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredSessions_AreRejected()
    {
        var user = await AuthTestSupport.RegisterAsync(_factory);
        (await user.Client.GetAsync($"/api/reservations/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound, "while valid it is authenticated");

        _clock.Advance(TimeSpan.FromHours(9)); // beyond the 8-hour absolute lifetime

        (await user.Client.GetAsync($"/api/reservations/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await user.Client.GetFromJsonAsync<JsonElement>("/api/identity/session")).GetProperty("isAuthenticated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SessionOfADeletedUser_IsRejected()
    {
        var user = await AuthTestSupport.RegisterAsync(_factory);
        await using (var db = _database.CreateIdentityDbContext())
            await db.Database.ExecuteSqlAsync($"DELETE FROM identity.users WHERE id = {user.Id}");

        (await user.Client.GetAsync($"/api/reservations/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AuthEndpoints_AreRateLimited_PerClient()
    {
        using var limited = new CatalogApiFactory(_database, new Dictionary<string, string?> { ["Identity:RateLimit:PermitsPerMinute"] = "3" });
        var client = limited.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(client);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await client.PostAsJsonAsync("/api/identity/login", new { email = "x@example.com", password = "wrong-password-1" })).StatusCode);

        statuses.Take(3).Should().OnlyContain(s => s == HttpStatusCode.Unauthorized);
        statuses.Skip(3).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests);
    }

    private sealed class ControllableClock : TimeProvider
    {
        private TimeSpan _offset = TimeSpan.Zero;
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _offset;
        public void Advance(TimeSpan by) => _offset += by;
    }
}

// Startup configuration must fail safely instead of defaulting a secret or weakening cookies.
public class SecurityConfigurationTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Config(params (string, string)[] values) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

    [Fact]
    public void Production_WithoutPersistedDataProtectionKeys_FailsToStart()
    {
        var act = () => SecurityOptions.Resolve(Config(), new Env("Production"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:KeysPath*");
    }

    [Fact]
    public void Production_WithKeysPath_UsesSecureCookiesAndTheHostPrefix()
    {
        var options = SecurityOptions.Resolve(Config(("DataProtection:KeysPath", "/keys"), ("Security:ForwardedHeaders:Mode", "Direct")), new Env("Production"));
        options.RelaxedTransport.Should().BeFalse();
        options.AuthCookieName.Should().StartWith("__Host-");
        options.SessionLifetime.Should().Be(TimeSpan.FromHours(8));
    }

    [Fact]
    public void Development_NeedsNoKeysPath_ButStillRelaxesOnlyTransport()
    {
        var options = SecurityOptions.Resolve(Config(), new Env("Development"));
        options.RelaxedTransport.Should().BeTrue();
        options.DataProtectionKeysPath.Should().BeNull();
    }

    [Theory]
    [InlineData("Identity:SessionLifetimeMinutes", "1")]
    [InlineData("Identity:SessionLifetimeMinutes", "999999")]
    [InlineData("Identity:RateLimit:PermitsPerMinute", "0")]
    public void InvalidNumericSettings_FailToStart(string key, string value)
    {
        var act = () => SecurityOptions.Resolve(Config((key, value)), new Env("Development"));
        act.Should().Throw<InvalidOperationException>();
    }
}
