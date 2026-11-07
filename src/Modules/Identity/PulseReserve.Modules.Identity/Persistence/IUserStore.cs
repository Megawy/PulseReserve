using PulseReserve.Modules.Identity.Domain;

namespace PulseReserve.Modules.Identity.Persistence;

public interface IUserStore
{
    Task<User?> FindByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Adds the user; returns false if the normalized email is already registered (decided by a unique index).</summary>
    Task<bool> TryAddAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>Atomically counts a failed login and starts a lockout once the limit is reached.</summary>
    Task RecordFailedLoginAsync(Guid id, int maxFailures, DateTime lockoutEndUtc, CancellationToken cancellationToken = default);

    Task RecordSuccessfulLoginAsync(Guid id, string? upgradedPasswordHash, CancellationToken cancellationToken = default);
}
