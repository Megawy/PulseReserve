using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;

namespace PulseReserve.IntegrationTests.Api;

/// <summary>A registered user with its own cookie-carrying client and the current CSRF token applied.</summary>
internal sealed class TestUser(HttpClient client, Guid id, string email)
{
    public HttpClient Client { get; } = client;
    public Guid Id { get; } = id;
    public string Email { get; } = email;
}

internal static class AuthTestSupport
{
    public const string Password = "correct-horse-battery-1";
    public const string CsrfHeader = "X-XSRF-TOKEN";

    /// <summary>Loads an anonymous session (which also issues the CSRF cookie/token) and applies the token to the client.</summary>
    public static async Task<string> RefreshCsrfAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/identity/session");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return ApplyToken(client, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    public static string ApplyToken(HttpClient client, JsonElement session)
    {
        var token = session.GetProperty("csrfToken").GetString()!;
        client.DefaultRequestHeaders.Remove(CsrfHeader);
        client.DefaultRequestHeaders.Add(CsrfHeader, token);
        return token;
    }

    public static async Task<TestUser> RegisterAsync(CatalogApiFactory factory, string? email = null, string password = Password)
    {
        var client = factory.CreateClient();
        await RefreshCsrfAsync(client);
        email ??= $"user-{Guid.NewGuid():N}@example.com";

        var response = await client.PostAsJsonAsync("/api/identity/register", new { email, password, displayName = "Test User" });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        ApplyToken(client, session);
        return new TestUser(client, session.GetProperty("user").GetProperty("id").GetGuid(), email);
    }
}

internal static class AdminTestSupport
{
    private const string Key = "Identity:AdminUserIds";

    private static IConfiguration Config(CatalogApiFactory factory) => factory.Services.GetRequiredService<IConfiguration>();

    public static void Grant(CatalogApiFactory factory, Guid userId)
    {
        var config = Config(factory);
        var current = (config[Key] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        config[Key] = string.Join(',', current.Append(userId.ToString()));
    }

    public static void Revoke(CatalogApiFactory factory, Guid userId)
    {
        var config = Config(factory);
        config[Key] = string.Join(',', (config[Key] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(id => !string.Equals(id, userId.ToString(), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Registers a normal user, then lists its ID in the deployment configuration (the only way to become Admin).</summary>
    public static async Task<TestUser> RegisterAdminAsync(CatalogApiFactory factory)
    {
        var user = await AuthTestSupport.RegisterAsync(factory);
        Grant(factory, user.Id);
        return user;
    }

    public static TestUser RegisterAdmin(CatalogApiFactory factory) => RegisterAdminAsync(factory).GetAwaiter().GetResult();
}
