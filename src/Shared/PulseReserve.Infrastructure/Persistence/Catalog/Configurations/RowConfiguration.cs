using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Configurations;

public sealed class RowConfiguration : IEntityTypeConfiguration<Row>
{
    public void Configure(EntityTypeBuilder<Row> builder)
    {
        builder.ToTable("rows");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id)
            .HasColumnName("id");

        builder.Property<SeatingLayoutId>("SeatingLayoutId")
            .HasColumnName("seating_layout_id")
            .IsRequired();

        builder.Property(r => r.SectionId)
            .HasColumnName("section_id")
            .IsRequired();

        builder.Property(r => r.Label)
            .HasColumnName("label")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(r => r.DisplayOrder)
            .HasColumnName("display_order")
            .HasDefaultValue(0)
            .IsRequired();

        // Composite Foreign Key ensuring Row belongs to the Section within the same SeatingLayout
        builder.HasOne<Section>()
            .WithMany()
            .HasForeignKey("SeatingLayoutId", nameof(Row.SectionId))
            .HasPrincipalKey("SeatingLayoutId", nameof(Section.Id))
            .HasConstraintName("fk_rows_sections_composite")
            .OnDelete(DeleteBehavior.Cascade);

        // Supports composite FK from catalog.seats
        builder.HasAlternateKey("SeatingLayoutId", nameof(Row.SectionId), nameof(Row.Id))
            .HasName("uq_rows_layout_section_id");

        builder.HasIndex(nameof(Row.SectionId), nameof(Row.Label))
            .IsUnique()
            .HasDatabaseName("uq_rows_section_label_lower");

        builder.HasIndex("SeatingLayoutId")
            .HasDatabaseName("idx_rows_layout_id");
    }
}
