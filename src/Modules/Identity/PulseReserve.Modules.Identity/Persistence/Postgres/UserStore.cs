using Microsoft.EntityFrameworkCore;
using Npgsql;
using PulseReserve.Modules.Identity.Domain;

namespace PulseReserve.Modules.Identity.Persistence.Postgres;

internal sealed class UserStore(IdentityDbContext db) : IUserStore
{
    public Task<User?> FindByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public async Task<bool> TryAddAsync(User user, CancellationToken cancellationToken = default)
    {
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(user).State = EntityState.Detached;
            return false;
        }
    }

    public Task RecordFailedLoginAsync(Guid id, int maxFailures, DateTime lockoutEndUtc, CancellationToken cancellationToken = default) =>
        db.Database.ExecuteSqlAsync(
            $"""
             UPDATE identity.users
             SET failed_login_count = CASE WHEN failed_login_count + 1 >= {maxFailures} THEN 0 ELSE failed_login_count + 1 END,
                 lockout_end_utc = CASE WHEN failed_login_count + 1 >= {maxFailures} THEN {lockoutEndUtc} ELSE lockout_end_utc END
             WHERE id = {id}
             """,
            cancellationToken);

    public Task RecordSuccessfulLoginAsync(Guid id, string? upgradedPasswordHash, CancellationToken cancellationToken = default) =>
        db.Database.ExecuteSqlAsync(
            $"""
             UPDATE identity.users
             SET failed_login_count = 0,
                 lockout_end_utc = NULL,
                 password_hash = COALESCE({upgradedPasswordHash}, password_hash)
             WHERE id = {id}
             """,
            cancellationToken);
}
