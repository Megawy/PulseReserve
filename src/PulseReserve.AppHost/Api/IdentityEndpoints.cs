using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using PulseReserve.AppHost.Security;
using PulseReserve.Modules.Identity.Features;

namespace PulseReserve.AppHost.Api;

public sealed record RegisterApiRequest(string? Email, string? Password, string? DisplayName);

public sealed record LoginApiRequest(string? Email, string? Password);

public sealed record UserResponse(Guid Id, string Email, string DisplayName);

/// <summary>Current session. <c>CsrfToken</c> must be sent in the X-XSRF-TOKEN header on every POST.</summary>
public sealed record SessionResponse(bool IsAuthenticated, UserResponse? User, string CsrfToken, bool IsAdmin = false);

/// <summary>Minimal-API adapter for registration, login, logout and the current session. No business logic lives here.</summary>
public static class IdentityEndpoints
{
    public static WebApplication MapIdentityApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/identity").WithTags("Identity").AddEndpointFilter<CsrfValidationFilter>();

        api.MapGet("/session", (HttpContext http) =>
        {
            var token = http.IssueCsrfToken();
            var id = http.User.GetUserId();
            var user = id is null || http.User.Identity?.IsAuthenticated != true
                ? null
                : new UserResponse(id.Value, http.User.Identity!.Name ?? string.Empty, http.User.FindFirst("display_name")?.Value ?? string.Empty);
            return Results.Ok(new SessionResponse(user is not null, user, token, user is not null && http.User.IsInRole(AuthPolicies.AdminRole)));
        })
        .WithName("GetSession")
        .Produces<SessionResponse>();

        api.MapPost("/register", async (
            [FromBody] RegisterApiRequest request,
            IdentityService identity,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await identity.RegisterAsync(request.Email, request.Password, request.DisplayName, ct);
            return result.IsSuccess
                ? await SignInAsync(http, result.Value, StatusCodes.Status201Created)
                : result.Error.ToProblemResult();
        })
        .RequireRateLimiting(AuthPolicies.AuthRateLimit)
        .WithName("Register")
        .Produces<SessionResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/login", async (
            [FromBody] LoginApiRequest request,
            IdentityService identity,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await identity.LoginAsync(request.Email, request.Password, ct);
            return result.IsSuccess
                ? await SignInAsync(http, result.Value, StatusCodes.Status200OK)
                : Results.Problem(
                    title: result.Error.Code,
                    detail: result.Error.Message,
                    statusCode: StatusCodes.Status401Unauthorized);
        })
        .RequireRateLimiting(AuthPolicies.AuthRateLimit)
        .WithName("Login")
        .Produces<SessionResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized);

        api.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var token = http.IssueCsrfToken(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()));
            return Results.Ok(new SessionResponse(false, null, token));
        })
        .WithName("Logout")
        .Produces<SessionResponse>();

        return app;
    }

    private static async Task<IResult> SignInAsync(HttpContext http, AuthenticatedUser user, int statusCode)
    {
        var principal = user.ToPrincipal(http.RequestServices.GetRequiredService<AdminRegistry>().IsAdmin(user.Id));
        var security = http.RequestServices.GetRequiredService<SecurityOptions>();
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(security.SessionLifetime)
        });
        var token = http.IssueCsrfToken(principal);
        return Results.Json(
            new SessionResponse(true, new UserResponse(user.Id, user.Email, user.DisplayName), token, principal.IsInRole(AuthPolicies.AdminRole)),
            statusCode: statusCode);
    }
}
