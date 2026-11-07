using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Configurations;

public sealed class SeatConfiguration : IEntityTypeConfiguration<Seat>
{
    public void Configure(EntityTypeBuilder<Seat> builder)
    {
        builder.ToTable("seats", t =>
        {
            t.HasCheckConstraint("chk_seats_coordinates", "coordinate_x >= 0 AND coordinate_y >= 0");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasColumnName("id");

        builder.Property<SeatingLayoutId>("SeatingLayoutId")
            .HasColumnName("seating_layout_id")
            .IsRequired();

        builder.Property(s => s.SectionId)
            .HasColumnName("section_id")
            .IsRequired();

        builder.Property(s => s.RowId)
            .HasColumnName("row_id")
            .IsRequired();

        builder.Property(s => s.PriceTierId)
            .HasColumnName("price_tier_id")
            .IsRequired();

        builder.Property(s => s.Label)
            .HasColumnName("label")
            .HasMaxLength(50)
            .IsRequired();

        builder.ComplexProperty(s => s.Coordinate, coordBuilder =>
        {
            coordBuilder.Property(c => c.X)
                .HasColumnName("coordinate_x")
                .IsRequired();

            coordBuilder.Property(c => c.Y)
                .HasColumnName("coordinate_y")
                .IsRequired();
        });

        // Composite Foreign Key ensuring Seat belongs to the Row and Section in the same SeatingLayout
        builder.HasOne<Row>()
            .WithMany()
            .HasForeignKey("SeatingLayoutId", nameof(Seat.SectionId), nameof(Seat.RowId))
            .HasPrincipalKey("SeatingLayoutId", nameof(Row.SectionId), nameof(Row.Id))
            .HasConstraintName("fk_seats_rows_composite")
            .OnDelete(DeleteBehavior.Cascade);

        // Composite Foreign Key ensuring Seat references a PriceTier belonging to the same SeatingLayout
        builder.HasOne<PriceTier>()
            .WithMany()
            .HasForeignKey("SeatingLayoutId", nameof(Seat.PriceTierId))
            .HasPrincipalKey("SeatingLayoutId", nameof(PriceTier.Id))
            .HasConstraintName("fk_seats_price_tiers_composite")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex("SeatingLayoutId")
            .HasDatabaseName("idx_seats_layout_id");

        builder.HasIndex(nameof(Seat.RowId), nameof(Seat.Label))
            .IsUnique()
            .HasDatabaseName("uq_seats_row_label_lower");

        builder.HasIndex(nameof(Seat.PriceTierId))
            .HasDatabaseName("idx_seats_price_tier_id");
    }
}
