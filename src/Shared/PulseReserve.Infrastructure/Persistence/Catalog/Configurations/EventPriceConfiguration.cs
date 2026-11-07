using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Configurations;

public sealed class EventPriceConfiguration : IEntityTypeConfiguration<EventPrice>
{
    public void Configure(EntityTypeBuilder<EventPrice> builder)
    {
        builder.ToTable("event_prices", t =>
        {
            t.HasCheckConstraint("chk_event_prices_amount_non_negative", "price_amount >= 0");
            t.HasCheckConstraint("chk_event_prices_currency_iso", "length(price_currency) = 3 AND price_currency = upper(price_currency)");
        });

        builder.HasKey("EventId", nameof(EventPrice.PriceTierId))
            .HasName("pk_event_prices");

        builder.Property<EventId>("EventId")
            .HasColumnName("event_id")
            .IsRequired();

        builder.Property<SeatingLayoutId>("SeatingLayoutId")
            .HasColumnName("seating_layout_id")
            .IsRequired();

        builder.Property(ep => ep.PriceTierId)
            .HasColumnName("price_tier_id")
            .IsRequired();

        builder.ComplexProperty(ep => ep.Price, priceBuilder =>
        {
            priceBuilder.Property(m => m.Amount)
                .HasColumnName("price_amount")
                .HasPrecision(18, 4)
                .IsRequired();

            priceBuilder.Property(m => m.Currency)
                .HasColumnName("price_currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        // Composite Foreign Key 2: Guarantees that the price tier belongs to the SeatingLayout assigned to the Event
        builder.HasOne<PriceTier>()
            .WithMany()
            .HasForeignKey("SeatingLayoutId", nameof(EventPrice.PriceTierId))
            .HasPrincipalKey("SeatingLayoutId", nameof(PriceTier.Id))
            .HasConstraintName("fk_event_prices_price_tiers_composite")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
