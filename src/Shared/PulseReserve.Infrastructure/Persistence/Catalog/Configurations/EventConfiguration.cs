using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.Venues;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Configurations;

public sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events", t =>
        {
            t.HasCheckConstraint("chk_events_chronological", "scheduled_start_utc < scheduled_end_utc");
            t.HasCheckConstraint("chk_events_status_valid", "status IN ('Draft', 'Published', 'OnSale', 'Closed', 'Cancelled')");
            t.HasCheckConstraint("chk_events_published_layout", "status = 'Draft' OR seating_layout_id IS NOT NULL");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id");

        builder.Property(e => e.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.Description)
            .HasColumnName("description")
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(e => e.VenueId)
            .HasColumnName("venue_id")
            .IsRequired();

        builder.Property(e => e.SeatingLayoutId)
            .HasColumnName("seating_layout_id")
            .IsRequired(false);

        builder.Property(e => e.ScheduledStartUtc)
            .HasColumnName("scheduled_start_utc")
            .IsRequired();

        builder.Property(e => e.ScheduledEndUtc)
            .HasColumnName("scheduled_end_utc")
            .IsRequired();

        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasMaxLength(30)
            .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.EnumToStringConverter<EventStatus>())
            .HasDefaultValue(EventStatus.Draft)
            .IsRequired();

        builder.Property(e => e.PublishedAtUtc)
            .HasColumnName("published_at_utc")
            .IsRequired(false);

        builder.Property(e => e.OnSaleAtUtc)
            .HasColumnName("on_sale_at_utc")
            .IsRequired(false);

        builder.Property(e => e.CancelledAtUtc)
            .HasColumnName("cancelled_at_utc")
            .IsRequired(false);

        builder.Property(e => e.CancellationReason)
            .HasColumnName("cancellation_reason")
            .HasMaxLength(500)
            .IsRequired(false);

        builder.Property<DateTime>("CreatedAtUtc")
            .HasColumnName("created_at_utc")
            .HasDefaultValueSql("CLOCK_TIMESTAMP()")
            .IsRequired();

        builder.Property<DateTime>("UpdatedAtUtc")
            .HasColumnName("updated_at_utc")
            .HasDefaultValueSql("CLOCK_TIMESTAMP()")
            .IsRequired();

        builder.Property<int>("Version")
            .HasColumnName("version")
            .HasDefaultValue(1)
            .IsConcurrencyToken()
            .IsRequired();

        // Foreign Keys by ID only (no navigation properties)
        builder.HasOne<Venue>()
            .WithMany()
            .HasForeignKey(e => e.VenueId)
            .HasConstraintName("fk_events_venues")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SeatingLayout>()
            .WithMany()
            .HasForeignKey(e => e.SeatingLayoutId)
            .HasConstraintName("fk_events_seating_layouts")
            .OnDelete(DeleteBehavior.Restrict);

        // Crucial alternate key supporting composite foreign key from catalog.event_prices
        builder.HasAlternateKey(nameof(Event.Id), nameof(Event.SeatingLayoutId))
            .HasName("uq_events_id_layout");

        builder.HasIndex(e => e.VenueId)
            .HasDatabaseName("idx_events_venue_id");

        builder.HasIndex(e => e.SeatingLayoutId)
            .HasDatabaseName("idx_events_seating_layout_id");

        builder.HasIndex(e => new { e.Status, e.ScheduledStartUtc })
            .HasDatabaseName("idx_events_status_start");

        // Event-scoped pricing collection mapped via composite FK to catalog.event_prices
        builder.HasMany(e => e.Prices)
            .WithOne()
            .HasForeignKey("EventId", "SeatingLayoutId")
            .HasPrincipalKey(nameof(Event.Id), nameof(Event.SeatingLayoutId))
            .HasConstraintName("fk_event_prices_events_composite")
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Prices).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
