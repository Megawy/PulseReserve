using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Configurations;

public sealed class SectionConfiguration : IEntityTypeConfiguration<Section>
{
    public void Configure(EntityTypeBuilder<Section> builder)
    {
        builder.ToTable("sections");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasColumnName("id");

        builder.Property<SeatingLayoutId>("SeatingLayoutId")
            .HasColumnName("seating_layout_id")
            .IsRequired();

        builder.Property(s => s.Name)
            .HasColumnName("name")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(s => s.DisplayOrder)
            .HasColumnName("display_order")
            .HasDefaultValue(0)
            .IsRequired();

        // Supports composite FK from catalog.rows
        builder.HasAlternateKey("SeatingLayoutId", nameof(Section.Id))
            .HasName("uq_sections_layout_id");

        builder.HasIndex("SeatingLayoutId", nameof(Section.Name))
            .IsUnique()
            .HasDatabaseName("uq_sections_layout_name_lower");
    }
}
