using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PulseReserve.AppHost.Security;

namespace PulseReserve.AppHost.Live;

public static class LivePolicies
{
    public const string HubPath = "/hubs/live";
    public const string ConnectRateLimit = "live-connect";
}

/// <summary>
/// Cross-origin protection for the hub. WebSockets are not covered by CORS, so the server checks the <c>Origin</c> header itself: a
/// browser page of another site is refused. (The session cookie is also SameSite=Strict, which already withholds it from a cross-site
/// handshake; this is the second, independent layer.) A request with no <c>Origin</c> comes from a non-browser client, which cannot
/// borrow a user's cookie and is not a cross-site attack.
/// </summary>
public static class LiveOrigin
{
    public static bool IsAllowed(HttpRequest request, LiveOptions options)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false; // includes the opaque origin "null"

        if (options.AllowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant()))
            return true;

        // Same origin: the host and port the browser used are the host and port this request arrived for. The scheme is not compared
        // because behind a TLS-terminating proxy the request's scheme depends on forwarded-header trust (see Security:ForwardedHeaders).
        var requestPort = request.Host.Port ?? (uri.Scheme == "https" ? 443 : 80);
        return string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == requestPort;
    }
}

public static class LiveRegistration
{
    /// <summary>Registers the real-time layer. Returns the validated options.</summary>
    public static LiveOptions AddPulseReserveLive(this WebApplicationBuilder builder, string connectionString)
    {
        var options = LiveOptions.Resolve(builder.Configuration);
        var services = builder.Services;

        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LiveEpoch>();
        services.AddSingleton<LiveConnectionRegistry>();
        services.AddSingleton<LiveStatus>();

        if (options.HoldExpiryEnabled)
            services.AddHostedService<HoldExpiryWorker>();

        if (!options.Enabled)
            return options;

        services.AddSignalR(hub =>
        {
            hub.KeepAliveInterval = TimeSpan.FromSeconds(15);
            hub.ClientTimeoutInterval = TimeSpan.FromSeconds(45);
            hub.HandshakeTimeout = TimeSpan.FromSeconds(15);
            hub.MaximumReceiveMessageSize = 4 * 1024; // the client only ever sends subscription requests
            hub.MaximumParallelInvocationsPerClient = 1;
            hub.EnableDetailedErrors = false;
        });

        services.AddScoped<ILiveStateReader, ModuleLiveStateReader>();
        services.AddSingleton<LiveDispatcher>();
        services.AddSingleton<ILiveSignalSink>(sp => sp.GetRequiredService<LiveDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<LiveDispatcher>());
        services.AddHostedService<LiveConnectionReaper>();

        if (options.ChangeFeedEnabled)
        {
            services.AddSingleton<IChangeFeedConnectionFactory>(new NpgsqlChangeFeedConnectionFactory(connectionString));
            services.AddSingleton(ChangeFeedTimings.Default);
            services.AddHostedService<PostgresChangeListener>();
        }

        // Opening a connection is cheap but not free: bound it per client address (the address is the real one behind a trusted proxy).
        services.Configure<RateLimiterOptions>(limiter => limiter.AddPolicy(LivePolicies.ConnectRateLimit, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                SecurityExtensions.ClientAddressKey(http),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.ConnectsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));

        return options;
    }

    /// <summary>Refuses a browser page of another site before the hub is reached.</summary>
    public static IApplicationBuilder UsePulseReserveLive(this IApplicationBuilder app, LiveOptions options)
    {
        if (!options.Enabled)
            return app;

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(LivePolicies.HubPath) && !LiveOrigin.IsAllowed(context.Request, options))
            {
                await SecurityExtensions.WriteProblemAsync(context, StatusCodes.Status403Forbidden, "Live.OriginNotAllowed", "This origin may not open a live connection.");
                return;
            }

            await next();
        });
    }

    public static IEndpointRouteBuilder MapPulseReserveLive(this IEndpointRouteBuilder app, LiveOptions options)
    {
        // Operators can see whether the layer works without a secret. Administrators only.
        app.MapGet("/api/admin/live/status", (LiveStatus status, LiveConnectionRegistry registry, LiveEpoch epoch, LiveOptions live) => Results.Ok(new
        {
            enabled = live.Enabled,
            changeFeedEnabled = live.ChangeFeedEnabled,
            holdExpiryEnabled = live.HoldExpiryEnabled,
            epoch = epoch.Value,
            connections = registry.Count,
            listenerConnected = status.ListenerConnected,
            listenerReconnects = status.ListenerReconnects,
            lastNotificationUtc = status.LastNotificationUtc,
            signalsReceived = status.SignalsReceived,
            signalsDropped = status.SignalsDropped,
            messagesSent = status.MessagesSent,
            resyncs = status.Resyncs,
            maxConnectionSeconds = (int)live.MaxConnectionAge.TotalSeconds
        })).RequireAuthorization(AuthPolicies.Admin).WithName("LiveStatus");

        if (!options.Enabled)
            return app;

        app.MapHub<LiveHub>(LivePolicies.HubPath).AllowAnonymous().RequireRateLimiting(LivePolicies.ConnectRateLimit);
        return app;
    }
}
