using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using PulseReserve.Modules.Identity.Domain;
using PulseReserve.Modules.Identity.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Identity.Features;

/// <summary>The authenticated identity the host turns into a cookie principal.</summary>
public sealed record AuthenticatedUser(Guid Id, string Email, string DisplayName);

/// <summary>
/// Registration, login and user lookup. Passwords are hashed with ASP.NET's <see cref="PasswordHasher{TUser}"/>
/// (PBKDF2, per-user salt, upgradeable). Login failures are indistinguishable from the outside.
/// </summary>
public sealed class IdentityService(IUserStore store, IPasswordHasher<User> hasher, TimeProvider time)
{
    // A hash of a random throwaway value, so a login for an unknown email costs the same as a wrong password.
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(new User(), Guid.NewGuid().ToString("N"));

    public async Task<Result<AuthenticatedUser>> RegisterAsync(
        string? email, string? password, string? displayName, CancellationToken cancellationToken = default)
    {
        email = email?.Trim();
        displayName = displayName?.Trim();

        if (!IsValidEmail(email))
            return Result.Failure<AuthenticatedUser>(IdentityErrors.InvalidEmail);
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > IdentityPolicy.MaxDisplayNameLength)
            return Result.Failure<AuthenticatedUser>(IdentityErrors.InvalidDisplayName);
        if (password is null || password.Length < IdentityPolicy.MinPasswordLength || password.Length > IdentityPolicy.MaxPasswordLength)
            return Result.Failure<AuthenticatedUser>(IdentityErrors.WeakPassword);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = IdentityPolicy.NormalizeEmail(email),
            DisplayName = displayName,
            CreatedAtUtc = time.GetUtcNow().UtcDateTime
        };
        user.PasswordHash = hasher.HashPassword(user, password);

        return await store.TryAddAsync(user, cancellationToken)
            ? Result.Success(ToUser(user))
            : Result.Failure<AuthenticatedUser>(IdentityErrors.EmailTaken);
    }

    public async Task<Result<AuthenticatedUser>> LoginAsync(
        string? email, string? password, CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(email) ? null : IdentityPolicy.NormalizeEmail(email);
        var user = normalized is null ? null : await store.FindByNormalizedEmailAsync(normalized, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;

        if (user is null || string.IsNullOrEmpty(password) || password.Length > IdentityPolicy.MaxPasswordLength)
        {
            hasher.VerifyHashedPassword(new User(), DummyHash, password ?? string.Empty);
            return Result.Failure<AuthenticatedUser>(IdentityErrors.InvalidCredentials);
        }

        if (user.LockoutEndUtc is { } lockoutEnd && lockoutEnd > now)
        {
            hasher.VerifyHashedPassword(user, DummyHash, password);
            return Result.Failure<AuthenticatedUser>(IdentityErrors.InvalidCredentials);
        }

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            await store.RecordFailedLoginAsync(user.Id, IdentityPolicy.MaxFailedLogins, now + IdentityPolicy.LockoutDuration, cancellationToken);
            return Result.Failure<AuthenticatedUser>(IdentityErrors.InvalidCredentials);
        }

        var upgraded = verification == PasswordVerificationResult.SuccessRehashNeeded ? hasher.HashPassword(user, password) : null;
        await store.RecordSuccessfulLoginAsync(user.Id, upgraded, cancellationToken);
        return Result.Success(ToUser(user));
    }

    public async Task<AuthenticatedUser?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await store.FindByIdAsync(id, cancellationToken);
        return user is null ? null : ToUser(user);
    }

    private static AuthenticatedUser ToUser(User user) => new(user.Id, user.Email, user.DisplayName);

    private static bool IsValidEmail([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > IdentityPolicy.MaxEmailLength)
            return false;
        return MailAddress.TryCreate(email, out var parsed) && parsed.Address == email && parsed.Host.Contains('.');
    }
}
