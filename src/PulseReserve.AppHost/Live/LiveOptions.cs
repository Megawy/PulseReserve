using System.Globalization;

namespace PulseReserve.AppHost.Live;

/// <summary>
/// Validated settings of the real-time layer. Everything has a safe default; a value that cannot be understood fails startup
/// instead of being read as on or off.
/// <list type="bullet">
/// <item><c>Live:Enabled</c> (default true) maps the SignalR hub <c>/hubs/live</c>. When false the SPA keeps working with its
/// request/response refresh and polling fallbacks.</item>
/// <item><c>Live:ChangeFeed:Enabled</c> (default true) listens to the database's commit notifications. Off, nothing is pushed
/// (the hub still accepts connections); used by hosts that must not open a database connection at startup.</item>
/// <item><c>Reservation:HoldExpiry:Enabled</c> (default true), <c>:IntervalSeconds</c> (5), <c>:BatchSize</c> (100) close holds that ran
/// out, so that "the hold ended" is a committed, announced fact. Correctness never depends on it (lazy expiry stays).</item>
/// <item><c>Live:MaxConnectionMinutes</c> (default 10): every connection is closed after this age and the client reconnects, which
/// re-evaluates its identity. This bounds how long a revoked administrator or an ended session keeps receiving messages.</item>
/// <item><c>Live:MaxEventSubscriptionsPerConnection</c> (default 5), <c>Live:CoalesceMilliseconds</c> (150),
/// <c>Live:ChannelCapacity</c> (10000), <c>Live:ConnectsPerMinute</c> (60 per client address).</item>
/// <item><c>Live:AllowedOrigins</c> (comma separated absolute origins): browsers whose <c>Origin</c> differs from the request host
/// are refused unless listed. The SPA is normally served from the same origin, so this is usually empty.</item>
/// </list>
/// </summary>
public sealed record LiveOptions(
    bool Enabled,
    bool ChangeFeedEnabled,
    bool HoldExpiryEnabled,
    TimeSpan HoldExpiryInterval,
    int HoldExpiryBatch,
    TimeSpan MaxConnectionAge,
    int MaxEventSubscriptions,
    TimeSpan Coalesce,
    int ChannelCapacity,
    int ConnectsPerMinute,
    IReadOnlyList<string> AllowedOrigins)
{
    public static LiveOptions Resolve(IConfiguration configuration)
    {
        return new LiveOptions(
            Flag(configuration, "Live:Enabled", true),
            Flag(configuration, "Live:ChangeFeed:Enabled", true),
            Flag(configuration, "Reservation:HoldExpiry:Enabled", true),
            TimeSpan.FromSeconds(Range(configuration, "Reservation:HoldExpiry:IntervalSeconds", 5, 1, 300)),
            Range(configuration, "Reservation:HoldExpiry:BatchSize", 100, 1, 1000),
            TimeSpan.FromMinutes(Range(configuration, "Live:MaxConnectionMinutes", 10, 1, 60)),
            Range(configuration, "Live:MaxEventSubscriptionsPerConnection", 5, 1, 20),
            TimeSpan.FromMilliseconds(Range(configuration, "Live:CoalesceMilliseconds", 150, 10, 5000)),
            Range(configuration, "Live:ChannelCapacity", 10_000, 100, 1_000_000),
            Range(configuration, "Live:ConnectsPerMinute", 60, 1, 10_000),
            Origins(configuration["Live:AllowedOrigins"]));
    }

    private static bool Flag(IConfiguration configuration, string key, bool defaultValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        return bool.TryParse(raw, out var value) ? value : throw new InvalidOperationException($"{key} must be 'true' or 'false'.");
    }

    private static int Range(IConfiguration configuration, string key, int defaultValue, int min, int max)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new InvalidOperationException($"{key} must be a whole number from {min} to {max}.");
        return value;
    }

    private static IReadOnlyList<string> Origins(string? raw)
    {
        var origins = new List<string>();
        foreach (var entry in (raw ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.UserInfo.Length > 0)
            {
                throw new InvalidOperationException($"Live:AllowedOrigins contains '{entry}', which is not an origin such as https://app.example.com.");
            }

            origins.Add(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant());
        }

        return origins;
    }
}
