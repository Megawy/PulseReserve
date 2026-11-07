using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Identity.Domain;

/// <summary>A registered user. Credentials are stored only as a salted PBKDF2 hash produced by ASP.NET's PasswordHasher.</summary>
public sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public static class IdentityPolicy
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 128;
    public const int MaxEmailLength = 254;
    public const int MaxDisplayNameLength = 100;
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();
}

public static class IdentityErrors
{
    public static readonly Error InvalidEmail =
        new("Identity.InvalidEmail", "A valid email address is required.");

    public static readonly Error InvalidDisplayName =
        new("Identity.InvalidDisplayName", $"A display name of 1 to {IdentityPolicy.MaxDisplayNameLength} characters is required.");

    public static readonly Error WeakPassword =
        new("Identity.WeakPassword", $"The password must be {IdentityPolicy.MinPasswordLength} to {IdentityPolicy.MaxPasswordLength} characters long.");

    public static readonly Error EmailTaken =
        Error.Conflict("Identity.EmailTaken", "An account with this email address may already exist. Try signing in instead.");

    /// <summary>One answer for unknown email, wrong password and locked account, so the response reveals nothing.</summary>
    public static readonly Error InvalidCredentials =
        new("Identity.InvalidCredentials", "The email or password is incorrect.");
}
