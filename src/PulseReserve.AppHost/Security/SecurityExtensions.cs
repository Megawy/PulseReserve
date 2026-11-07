using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using PulseReserve.Modules.Identity.Features;

namespace PulseReserve.AppHost.Security;

public static class AuthPolicies
{
    /// <summary>A signed-in customer. Reservation endpoints require it.</summary>
    public const string Customer = "Customer";
    public const string CustomerRole = "Customer";

    /// <summary>A signed-in administrator. Every Catalog mutation requires it.</summary>
    public const string Admin = "Admin";
    public const string AdminRole = "Admin";
    public const string AuthRateLimit = "auth";

    /// <summary>State-changing financial requests, limited per signed-in user (see <see cref="SecurityExtensions.AddFinancialWriteRateLimit"/>).</summary>
    public const string FinancialWriteRateLimit = "financial-write";
}

public static class SecurityExtensions
{
    /// <summary>
    /// Same-origin cookie authentication: an HttpOnly, SameSite=Strict (and Secure outside dev) session cookie that
    /// JavaScript can never read, so no credential or token lives in browser storage. State-changing requests are
    /// protected by antiforgery tokens; sign-in endpoints are rate limited.
    /// </summary>
    public static SecurityOptions AddPulseReserveSecurity(this WebApplicationBuilder builder)
    {
        var options = SecurityOptions.Resolve(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<AdminRegistry>();
        builder.Services.AddSingleton<CsrfValidationFilter>();

        var dataProtection = builder.Services.AddDataProtection().SetApplicationName("PulseReserve");
        if (options.DataProtectionKeysPath is not null)
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionKeysPath));

        var securePolicy = options.RelaxedTransport ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = options.AuthCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = securePolicy;
                cookie.Cookie.Path = "/";
                cookie.ExpireTimeSpan = options.SessionLifetime;
                cookie.SlidingExpiration = false; // absolute lifetime: a stolen cookie cannot be kept alive forever
                // An API answers with status codes, never redirects.
                cookie.Events.OnRedirectToLogin = ctx => WriteProblemAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized,
                    "Auth.Unauthorized", "Sign in to continue.");
                cookie.Events.OnRedirectToAccessDenied = ctx => WriteProblemAsync(ctx.HttpContext, StatusCodes.Status403Forbidden,
                    "Auth.Forbidden", "You do not have permission to perform this action.");
                // A session dies with its user, and roles are NEVER trusted from the cookie: on every request the
                // principal is rebuilt from the database and the current Admin list, so granting or revoking
                // Admin takes effect on the very next request of an existing session.
                cookie.Events.OnValidatePrincipal = async ctx =>
                {
                    var id = ctx.Principal?.GetUserId();
                    var identity = ctx.HttpContext.RequestServices.GetRequiredService<IdentityService>();
                    var user = id is null ? null : await identity.FindAsync(id.Value, ctx.HttpContext.RequestAborted);
                    if (user is null)
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync();
                        return;
                    }

                    var admins = ctx.HttpContext.RequestServices.GetRequiredService<AdminRegistry>();
                    ctx.ReplacePrincipal(user.ToPrincipal(admins.IsAdmin(user.Id)));
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Customer, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(AuthPolicies.CustomerRole))
            .AddPolicy(AuthPolicies.Admin, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(AuthPolicies.AdminRole));

        builder.Services.AddAntiforgery(antiforgery =>
        {
            antiforgery.HeaderName = SecurityOptions.CsrfHeaderName;
            antiforgery.Cookie.Name = "pulsereserve.antiforgery";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.SecurePolicy = securePolicy;
        });

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddAuthRateLimit(options.AuthPermitsPerMinute);
            limiter.AddFinancialWriteRateLimit(options.FinancialWritesPerMinute);
        });

        return options;
    }

    /// <summary>
    /// A fixed-window limit per SIGNED-IN USER (not per address, so it is correct behind a proxy). It must run after
    /// authentication, which is why the pipeline orders <c>UseAuthentication</c> before <c>UseRateLimiter</c>. Anonymous
    /// requests never reach a financial write (they are refused with 401 by authorization) and share one bucket per address.
    /// </summary>
    public static void AddFinancialWriteRateLimit(this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions limiter, int permitsPerMinute) =>
        limiter.AddPolicy(AuthPolicies.FinancialWriteRateLimit, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                http.User.GetUserId() is { } user ? $"user:{user}" : $"ip:{ClientAddressKey(http)}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    /// <summary>
    /// An unexpected failure answers with a minimal problem document ("Server.Error") and never an exception message or stack
    /// (Development keeps the developer page instead). The failure is still logged by the middleware. A webhook that fails
    /// here is answered with a 5xx, which makes the provider redeliver it.
    /// </summary>
    public static IApplicationBuilder UseProblemOnUnhandledException(this IApplicationBuilder app) =>
        app.UseExceptionHandler(errorApp => errorApp.Run(context => WriteProblemAsync(
            context, StatusCodes.Status500InternalServerError, "Server.Error", "The request could not be completed.")));

    /// <summary>Sign-in and registration attempts per client ADDRESS per minute (see <see cref="ClientAddressKey"/>).</summary>
    public static void AddAuthRateLimit(this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions limiter, int permitsPerMinute) =>
        limiter.AddPolicy(AuthPolicies.AuthRateLimit, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClientAddressKey(http),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    /// <summary>
    /// The per-address partition key of the sign-in limit: the connection's remote address, which the forwarded-header middleware
    /// replaces with the real client address ONLY for connections from a configured proxy. A client cannot change it by sending
    /// <c>X-Forwarded-For</c> itself.
    /// </summary>
    public static string ClientAddressKey(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Trusts <c>X-Forwarded-For</c>/<c>-Proto</c> only from the configured proxies and networks (nothing else, and never the
    /// framework's implicit loopback trust). A no-op in <c>Direct</c> mode. It must be the FIRST middleware, so the logger, the
    /// rate limiter and the authentication cookie rules all see the real client address and scheme.
    /// </summary>
    public static IApplicationBuilder UsePulseReserveForwardedHeaders(this IApplicationBuilder app, ForwardedHeadersSettings settings)
    {
        if (!settings.BehindProxy)
            return app;

        var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            ForwardLimit = settings.ForwardLimit
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in settings.KnownProxies)
            options.KnownProxies.Add(proxy);
        foreach (var network in settings.KnownNetworks)
            options.KnownIPNetworks.Add(network);
        return app.UseForwardedHeaders(options);
    }

    public static Guid? GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty ? id : null;

    public static ClaimsPrincipal ToPrincipal(this AuthenticatedUser user, bool isAdmin)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Email),
            new("display_name", user.DisplayName),
            new(ClaimTypes.Role, AuthPolicies.CustomerRole)
        };
        if (isAdmin)
            claims.Add(new Claim(ClaimTypes.Role, AuthPolicies.AdminRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    internal static Task WriteProblemAsync(HttpContext http, int status, string title, string detail)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(
            new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = status, Title = title, Detail = detail },
            options: null,
            contentType: "application/problem+json");
    }

    /// <summary>
    /// Issues a fresh antiforgery token for the CURRENT principal (tokens are bound to the signed-in user, so one is
    /// issued again after login, registration and logout). The token is returned in the body and mirrored into the
    /// script-readable XSRF-TOKEN cookie, which Angular echoes back in the X-XSRF-TOKEN header.
    /// </summary>
    public static string IssueCsrfToken(this HttpContext http, ClaimsPrincipal? principal = null)
    {
        if (principal is not null)
            http.User = principal;

        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        var token = tokens.RequestToken!;
        var options = http.RequestServices.GetRequiredService<SecurityOptions>();
        http.Response.Cookies.Append(SecurityOptions.CsrfCookieName, token, new CookieOptions
        {
            HttpOnly = false, // must be readable by the SPA; it is a CSRF token, not a credential
            SameSite = SameSiteMode.Strict,
            Secure = !options.RelaxedTransport,
            Path = "/"
        });
        return token;
    }
}

/// <summary>Rejects unsafe-method requests that lack a valid antiforgery token (403, never reaches the handler).</summary>
public sealed class CsrfValidationFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) &&
            !HttpMethods.IsOptions(http.Request.Method) && !HttpMethods.IsTrace(http.Request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(
                    title: "Csrf.Invalid",
                    detail: "The request could not be verified. Reload the page and try again.",
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        return await next(context);
    }
}
