using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Configurations;

public sealed class PriceTierConfiguration : IEntityTypeConfiguration<PriceTier>
{
    public void Configure(EntityTypeBuilder<PriceTier> builder)
    {
        builder.ToTable("price_tiers", t =>
        {
            t.HasCheckConstraint("chk_price_tiers_name_non_empty", "length(trim(name)) > 0");
        });

        builder.HasKey(pt => pt.Id);
        builder.Property(pt => pt.Id)
            .HasColumnName("id");

        builder.Property<SeatingLayoutId>("SeatingLayoutId")
            .HasColumnName("seating_layout_id")
            .IsRequired();

        builder.Property(pt => pt.Name)
            .HasColumnName("name")
            .HasMaxLength(100)
            .IsRequired();

        // Supports composite FKs from catalog.seats and catalog.event_prices
        builder.HasAlternateKey("SeatingLayoutId", nameof(PriceTier.Id))
            .HasName("uq_price_tiers_layout_id");

        builder.HasIndex("SeatingLayoutId")
            .HasDatabaseName("idx_price_tiers_layout_id");
    }
}
