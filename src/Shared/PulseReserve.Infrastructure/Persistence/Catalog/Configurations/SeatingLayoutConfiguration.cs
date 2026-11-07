using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.Venues;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Configurations;

public sealed class SeatingLayoutConfiguration : IEntityTypeConfiguration<SeatingLayout>
{
    public void Configure(EntityTypeBuilder<SeatingLayout> builder)
    {
        builder.ToTable("seating_layouts");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id)
            .HasColumnName("id");

        builder.Property(l => l.VenueId)
            .HasColumnName("venue_id")
            .IsRequired();

        builder.Property(l => l.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(l => l.IsLocked)
            .HasColumnName("is_locked")
            .HasDefaultValue(false)
            .IsRequired();

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

        builder.HasOne<Venue>()
            .WithMany()
            .HasForeignKey(l => l.VenueId)
            .HasConstraintName("fk_seating_layouts_venues")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => l.VenueId)
            .HasDatabaseName("idx_seating_layouts_venue_id");

        // Aggregate Child Collections mapped through private backing fields
        builder.HasMany(l => l.Sections)
            .WithOne()
            .HasForeignKey("SeatingLayoutId")
            .HasConstraintName("fk_sections_seating_layouts")
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(l => l.Sections).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(l => l.Rows)
            .WithOne()
            .HasForeignKey("SeatingLayoutId")
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(l => l.Rows).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(l => l.Seats)
            .WithOne()
            .HasForeignKey("SeatingLayoutId")
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(l => l.Seats).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(l => l.PriceTiers)
            .WithOne()
            .HasForeignKey("SeatingLayoutId")
            .HasConstraintName("fk_price_tiers_seating_layouts")
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(l => l.PriceTiers).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
