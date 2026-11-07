using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PulseReserve.Modules.Catalog.Domain.Venues;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Configurations;

public sealed class VenueConfiguration : IEntityTypeConfiguration<Venue>
{
    public void Configure(EntityTypeBuilder<Venue> builder)
    {
        builder.ToTable("venues");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id)
            .HasColumnName("id");

        builder.Property(v => v.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.ComplexProperty(v => v.Address, addressBuilder =>
        {
            addressBuilder.Property(a => a.Street)
                .HasColumnName("address_street")
                .HasMaxLength(200)
                .IsRequired();

            addressBuilder.Property(a => a.City)
                .HasColumnName("address_city")
                .HasMaxLength(100)
                .IsRequired();

            addressBuilder.Property(a => a.Country)
                .HasColumnName("address_country")
                .HasMaxLength(100)
                .IsRequired();

            addressBuilder.Property(a => a.PostalCode)
                .HasColumnName("address_postal_code")
                .HasMaxLength(20)
                .HasDefaultValue(string.Empty)
                .IsRequired();
        });

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

        builder.HasIndex(v => v.Name)
            .HasDatabaseName("idx_venues_name");
    }
}
