using Microsoft.EntityFrameworkCore;
using PulseReserve.Modules.Reservation.Domain;

namespace PulseReserve.Modules.Reservation.Persistence.Postgres;

/// <summary>
/// EF Core context owning the <c>reservation</c> schema (ADR-008: module-owned persistence). It defines tables,
/// constraints and migrations; the concurrency-critical statements run as explicit SQL in <see cref="ReservationStore"/>.
/// </summary>
public sealed class ReservationDbContext(DbContextOptions<ReservationDbContext> options) : DbContext(options)
{
    public const string Schema = "reservation";

    internal DbSet<EventInventoryRow> EventInventories => Set<EventInventoryRow>();
    internal DbSet<EventSeatRow> EventSeats => Set<EventSeatRow>();
    internal DbSet<ReservationRow> Reservations => Set<ReservationRow>();
    internal DbSet<ReservationItemRow> ReservationItems => Set<ReservationItemRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<EventInventoryRow>(b =>
        {
            b.ToTable("event_inventories", t => t.HasCheckConstraint("chk_event_inventories_seat_count", "seat_count >= 0"));
            b.HasKey(x => x.EventId).HasName("pk_event_inventories");
            b.Property(x => x.EventId).HasColumnName("event_id");
            b.Property(x => x.SeatCount).HasColumnName("seat_count");
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ReservationRow>(b =>
        {
            b.ToTable("reservations", t =>
            {
                t.HasCheckConstraint("chk_reservations_status",
                    $"status IN ('{ReservationStatus.Held}', '{ReservationStatus.Released}', '{ReservationStatus.Expired}', '{ReservationStatus.Checkout}', '{ReservationStatus.Confirmed}')");
                t.HasCheckConstraint("chk_reservations_total_non_negative", "total_amount >= 0");
                t.HasCheckConstraint("chk_reservations_expiry_after_creation", "expires_at_utc > created_at_utc");
            });
            b.HasKey(x => x.Id).HasName("pk_reservations");
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.EventId).HasColumnName("event_id");
            b.Property(x => x.CustomerId).HasColumnName("customer_id");
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(ReservationPolicy.MaxIdempotencyKeyLength).IsRequired();
            b.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(18, 4);
            b.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
            b.Property(x => x.ClosedAtUtc).HasColumnName("closed_at_utc");

            // A retried request with the same key resolves to the same reservation.
            b.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("uq_reservations_customer_idempotency_key");

            // At most one active hold per customer per event (anti-hoarding), enforced by PostgreSQL.
            // A reservation in Checkout still counts as active: paying does not free the customer to hoard a second set.
            b.HasIndex(x => new { x.EventId, x.CustomerId }).IsUnique()
                .HasFilter($"status IN ('{ReservationStatus.Held}', '{ReservationStatus.Checkout}')")
                .HasDatabaseName("uq_reservations_active_hold_per_customer_event");

            b.HasIndex(x => new { x.Status, x.ExpiresAtUtc }).HasDatabaseName("idx_reservations_status_expires");

            b.HasOne<EventInventoryRow>().WithMany().HasForeignKey(x => x.EventId)
                .HasConstraintName("fk_reservations_event_inventories").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EventSeatRow>(b =>
        {
            b.ToTable("event_seats", t =>
            {
                t.HasCheckConstraint("chk_event_seats_status", $"status IN ('{SeatStatus.Available}', '{SeatStatus.Held}', '{SeatStatus.Sold}')");
                // A seat is owned by a reservation exactly when it is not available.
                t.HasCheckConstraint("chk_event_seats_owner", $"(status = '{SeatStatus.Available}') = (reservation_id IS NULL)");
                t.HasCheckConstraint("chk_event_seats_hold_expiry", $"status <> '{SeatStatus.Held}' OR hold_expires_at_utc IS NOT NULL");
                t.HasCheckConstraint("chk_event_seats_price_non_negative", "price_amount >= 0");
            });
            // One row per event seat: the authoritative state cell (ADR-009).
            b.HasKey(x => new { x.EventId, x.SeatId }).HasName("pk_event_seats");
            b.Property(x => x.EventId).HasColumnName("event_id");
            b.Property(x => x.SeatId).HasColumnName("seat_id");
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            b.Property(x => x.ReservationId).HasColumnName("reservation_id");
            b.Property(x => x.HoldExpiresAtUtc).HasColumnName("hold_expires_at_utc");
            b.Property(x => x.PriceAmount).HasColumnName("price_amount").HasPrecision(18, 4);
            b.Property(x => x.PriceCurrency).HasColumnName("price_currency").HasMaxLength(3).IsRequired();

            b.HasIndex(x => x.ReservationId).HasDatabaseName("idx_event_seats_reservation_id");

            b.HasOne<EventInventoryRow>().WithMany().HasForeignKey(x => x.EventId)
                .HasConstraintName("fk_event_seats_event_inventories").OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ReservationRow>().WithMany().HasForeignKey(x => x.ReservationId)
                .HasConstraintName("fk_event_seats_reservations").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReservationItemRow>(b =>
        {
            b.ToTable("reservation_items", t => t.HasCheckConstraint("chk_reservation_items_price_non_negative", "price_amount >= 0"));
            b.HasKey(x => new { x.ReservationId, x.SeatId }).HasName("pk_reservation_items");
            b.Property(x => x.ReservationId).HasColumnName("reservation_id");
            b.Property(x => x.EventId).HasColumnName("event_id");
            b.Property(x => x.SeatId).HasColumnName("seat_id");
            b.Property(x => x.PriceAmount).HasColumnName("price_amount").HasPrecision(18, 4);
            b.Property(x => x.PriceCurrency).HasColumnName("price_currency").HasMaxLength(3).IsRequired();

            b.HasOne<ReservationRow>().WithMany().HasForeignKey(x => x.ReservationId)
                .HasConstraintName("fk_reservation_items_reservations").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<EventSeatRow>().WithMany().HasForeignKey(x => new { x.EventId, x.SeatId })
                .HasConstraintName("fk_reservation_items_event_seats").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class EventInventoryRow
{
    public Guid EventId { get; set; }
    public int SeatCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

internal sealed class EventSeatRow
{
    public Guid EventId { get; set; }
    public Guid SeatId { get; set; }
    public string Status { get; set; } = SeatStatus.Available;
    public Guid? ReservationId { get; set; }
    public DateTime? HoldExpiresAtUtc { get; set; }
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = string.Empty;
}

internal sealed class ReservationRow
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid CustomerId { get; set; }
    public string Status { get; set; } = ReservationStatus.Held;
    public string IdempotencyKey { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
}

internal sealed class ReservationItemRow
{
    public Guid ReservationId { get; set; }
    public Guid EventId { get; set; }
    public Guid SeatId { get; set; }
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = string.Empty;
}
