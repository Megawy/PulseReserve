namespace PulseReserve.AppHost.Security;

/// <summary>
/// The trust boundary for administrators. Admin is NOT stored in the user table and cannot be requested through any
/// API: it is granted only to user IDs listed in the deployment configuration (<c>Identity:AdminUserIds</c>, e.g. the
/// environment variable <c>Identity__AdminUserIds=<guid>[,<guid>]</c>), which only whoever controls the
/// deployment can change. User IDs are server-generated GUIDs, so nobody can register themselves into the list
/// (unlike an email-based list, which could be pre-registered by an attacker).
/// <para>
/// The list is read on every check, so a configuration source that reloads (e.g. appsettings.json) grants or revokes
/// Admin on the next request; with environment variables a restart applies it. Either way roles are recomputed from
/// this list on every request, never read from the session cookie.
/// </para>
/// </summary>
public sealed class AdminRegistry(IConfiguration configuration)
{
    public const string ConfigKey = "Identity:AdminUserIds";

    public bool IsAdmin(Guid userId) => userId != Guid.Empty && Parse(configuration[ConfigKey]).Contains(userId);

    /// <summary>Startup validation: any entry that is not a non-empty GUID aborts startup instead of being ignored.</summary>
    public static IReadOnlySet<Guid> ParseOrThrow(string? raw)
    {
        var ids = new HashSet<Guid>();
        foreach (var entry in Split(raw))
        {
            if (!Guid.TryParse(entry, out var id) || id == Guid.Empty)
                throw new InvalidOperationException(
                    $"{ConfigKey} contains an invalid entry. Provide user IDs (GUIDs) separated by commas.");
            ids.Add(id);
        }
        return ids;
    }

    // Runtime reads ignore bad entries (never grant on doubt); startup already rejected them.
    private static HashSet<Guid> Parse(string? raw) =>
        Split(raw).Select(e => Guid.TryParse(e, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();

    private static IEnumerable<string> Split(string? raw) =>
        (raw ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
