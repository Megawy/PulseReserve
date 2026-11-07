using Microsoft.EntityFrameworkCore;
using PulseReserve.Modules.Identity.Domain;

namespace PulseReserve.Modules.Identity.Persistence.Postgres;

/// <summary>EF Core context owning the <c>identity</c> schema (ADR-008: module-owned persistence).</summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string Schema = "identity";

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users", t =>
            {
                t.HasCheckConstraint("chk_users_failed_login_count", "failed_login_count >= 0");
            });
            b.HasKey(x => x.Id).HasName("pk_users");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.Email).HasColumnName("email").HasMaxLength(IdentityPolicy.MaxEmailLength).IsRequired();
            b.Property(x => x.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(IdentityPolicy.MaxEmailLength).IsRequired();
            b.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(IdentityPolicy.MaxDisplayNameLength).IsRequired();
            b.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(512).IsRequired();
            b.Property(x => x.FailedLoginCount).HasColumnName("failed_login_count");
            b.Property(x => x.LockoutEndUtc).HasColumnName("lockout_end_utc");
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");

            // Uniqueness is decided by PostgreSQL, so concurrent registrations cannot create duplicates.
            b.HasIndex(x => x.NormalizedEmail).IsUnique().HasDatabaseName("uq_users_normalized_email");
        });
    }
}
