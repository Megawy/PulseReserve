using System.Globalization;
using System.Net;

namespace PulseReserve.AppHost.Security;

/// <summary>
/// How the host is exposed, decided explicitly by the deployment and never guessed:
/// <list type="bullet">
/// <item><c>Direct</c> - clients reach the application themselves. <c>X-Forwarded-*</c> headers are IGNORED (a client can send
/// anything), so the client address is the real peer address.</item>
/// <item><c>Proxy</c> - a reverse proxy sits in front. The client address and scheme are taken from <c>X-Forwarded-For/-Proto</c>
/// ONLY on connections coming from the listed proxies or networks; the same headers from any other peer are ignored.
/// At least one proxy or network must be listed. A catch-all network (<c>0.0.0.0/0</c>, <c>::/0</c>) is refused.</item>
/// </list>
/// Outside Development and Testing the mode must be set explicitly: the application will not guess the topology.
/// Settings: <c>Security:ForwardedHeaders:Mode</c>, <c>:KnownProxies</c> (addresses), <c>:KnownNetworks</c> (CIDR),
/// <c>:ForwardLimit</c> (default 1 = only the nearest proxy hop is trusted; raise it only for a chain whose every hop is listed).
/// </summary>
public sealed record ForwardedHeadersSettings(
    bool BehindProxy, IReadOnlyList<IPAddress> KnownProxies, IReadOnlyList<IPNetwork> KnownNetworks, int ForwardLimit)
{
    public const string ModeKey = "Security:ForwardedHeaders:Mode";

    public static ForwardedHeadersSettings Direct { get; } = new(false, [], [], 1);

    public static ForwardedHeadersSettings Resolve(IConfiguration configuration, bool relaxedEnvironment)
    {
        var mode = configuration[ModeKey];
        var proxies = Split(configuration["Security:ForwardedHeaders:KnownProxies"]);
        var networks = Split(configuration["Security:ForwardedHeaders:KnownNetworks"]);
        var limitText = configuration["Security:ForwardedHeaders:ForwardLimit"];

        if (string.IsNullOrWhiteSpace(mode))
        {
            if (!relaxedEnvironment)
            {
                throw new InvalidOperationException(
                    $"{ModeKey} is required outside Development and Testing: set it to 'Direct' (no reverse proxy) or 'Proxy' " +
                    "(then list Security:ForwardedHeaders:KnownProxies and/or KnownNetworks). The application does not guess how it is exposed.");
            }

            mode = "Direct";
        }

        if (string.Equals(mode, "Direct", StringComparison.OrdinalIgnoreCase))
        {
            if (proxies.Count > 0 || networks.Count > 0)
                throw new InvalidOperationException($"{ModeKey} is 'Direct' but proxies or networks are listed. Set the mode to 'Proxy' or remove the lists.");
            return Direct;
        }

        if (!string.Equals(mode, "Proxy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{ModeKey} must be 'Direct' or 'Proxy'.");

        var addresses = proxies.Select(p => IPAddress.TryParse(p, out var ip)
            ? ip
            : throw new InvalidOperationException($"Security:ForwardedHeaders:KnownProxies contains '{p}', which is not an IP address.")).ToList();
        var cidrs = networks.Select(n => IPNetwork.TryParse(n, out var net)
            ? net
            : throw new InvalidOperationException($"Security:ForwardedHeaders:KnownNetworks contains '{n}', which is not a CIDR network such as 10.0.0.0/8.")).ToList();

        if (addresses.Count == 0 && cidrs.Count == 0)
            throw new InvalidOperationException($"{ModeKey} is 'Proxy' but no Security:ForwardedHeaders:KnownProxies or KnownNetworks are listed: trusting every sender would let any client choose its own address.");
        if (cidrs.Any(n => n.PrefixLength == 0))
            throw new InvalidOperationException("Security:ForwardedHeaders:KnownNetworks must not contain a catch-all network (/0): that trusts every sender.");

        var limit = 1;
        if (!string.IsNullOrWhiteSpace(limitText) && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 10))
            throw new InvalidOperationException("Security:ForwardedHeaders:ForwardLimit must be a number from 1 to 10.");

        return new ForwardedHeadersSettings(true, addresses, cidrs, limit);
    }

    private static List<string> Split(string? value) =>
        (value ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
