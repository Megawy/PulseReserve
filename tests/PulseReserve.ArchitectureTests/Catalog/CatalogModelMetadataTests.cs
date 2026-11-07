using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PulseReserve.Infrastructure.Persistence.Catalog;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.Venues;
using Xunit;

namespace PulseReserve.ArchitectureTests.Catalog;

public class CatalogModelMetadataTests
{
    private readonly IModel _model;

    public CatalogModelMetadataTests()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=test_catalog_db")
            .Options;

        using var context = new CatalogDbContext(options);
        _model = context.Model;
    }

    [Theory]
    [InlineData(typeof(Venue), "venues")]
    [InlineData(typeof(SeatingLayout), "seating_layouts")]
    [InlineData(typeof(Section), "sections")]
    [InlineData(typeof(Row), "rows")]
    [InlineData(typeof(Seat), "seats")]
    [InlineData(typeof(PriceTier), "price_tiers")]
    [InlineData(typeof(Event), "events")]
    [InlineData(typeof(EventPrice), "event_prices")]
    public void Model_TablesMustExist_WithExpectedNamesAndCatalogSchema(Type entityType, string expectedTableName)
    {
        var entity = _model.FindEntityType(entityType);
        entity.Should().NotBeNull($"Entity '{entityType.Name}' must be mapped in CatalogDbContext.");

        var nonNullEntity = entity!;
        nonNullEntity.GetTableName().Should().Be(expectedTableName);
        var schema = nonNullEntity.GetSchema() ?? _model.GetDefaultSchema();
        schema.Should().Be("catalog");
    }

    [Fact]
    public void Model_PrimaryKeys_MustBeCorrect()
    {
        _model.FindEntityType(typeof(Venue))!.FindPrimaryKey()!.Properties
            .Select(p => p.Name).Should().Equal("Id");

        _model.FindEntityType(typeof(SeatingLayout))!.FindPrimaryKey()!.Properties
            .Select(p => p.Name).Should().Equal("Id");

        _model.FindEntityType(typeof(Section))!.FindPrimaryKey()!.Properties
            .Select(p => p.Name).Should().Equal("Id");

        _model.FindEntityType(typeof(Row))!.FindPrimaryKey()!.Properties
            .Select(p => p.Name).Should().Equal("Id");

        _model.FindEntityType(typeof(Seat))!.FindPrimaryKey()!.Properties
            .Select(p => p.Name).Should().Equal("Id");

        _model.FindEntityType(typeof(PriceTier))!.FindPrimaryKey()!.Properties
            .Select(p => p.Name).Should().Equal("Id");

        _model.FindEntityType(typeof(Event))!.FindPrimaryKey()!.Properties
            .Select(p => p.Name).Should().Equal("Id");

        var eventPricePk = _model.FindEntityType(typeof(EventPrice))!.FindPrimaryKey()!;
        eventPricePk.Properties.Select(p => p.Name).Should().BeEquivalentTo(["EventId", "PriceTierId"]);
    }

    [Fact]
    public void Model_HierarchyCompositeForeignKeys_MustExist()
    {
        // 1. Row -> Section composite FK: (seating_layout_id, section_id) -> catalog.sections(seating_layout_id, id)
        var rowEntity = _model.FindEntityType(typeof(Row))!;
        var rowSectionFk = rowEntity.GetForeignKeys()
            .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(Section));
        rowSectionFk.Should().NotBeNull();
        rowSectionFk!.Properties.Select(p => p.Name).Should().Equal("SeatingLayoutId", "SectionId");
        rowSectionFk.PrincipalKey.Properties.Select(p => p.Name).Should().Equal("SeatingLayoutId", "Id");
        rowSectionFk.GetConstraintName().Should().Be("fk_rows_sections_composite");
        rowSectionFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 2. Seat -> Row composite FK: (seating_layout_id, section_id, row_id) -> catalog.rows(seating_layout_id, section_id, id)
        var seatEntity = _model.FindEntityType(typeof(Seat))!;
        var seatRowFk = seatEntity.GetForeignKeys()
            .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(Row));
        seatRowFk.Should().NotBeNull();
        seatRowFk!.Properties.Select(p => p.Name).Should().Equal("SeatingLayoutId", "SectionId", "RowId");
        seatRowFk.PrincipalKey.Properties.Select(p => p.Name).Should().Equal("SeatingLayoutId", "SectionId", "Id");
        seatRowFk.GetConstraintName().Should().Be("fk_seats_rows_composite");
        seatRowFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 3. Seat -> PriceTier composite FK: (seating_layout_id, price_tier_id) -> catalog.price_tiers(seating_layout_id, id)
        var seatPriceTierFk = seatEntity.GetForeignKeys()
            .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(PriceTier));
        seatPriceTierFk.Should().NotBeNull();
        seatPriceTierFk!.Properties.Select(p => p.Name).Should().Equal("SeatingLayoutId", "PriceTierId");
        seatPriceTierFk.PrincipalKey.Properties.Select(p => p.Name).Should().Equal("SeatingLayoutId", "Id");
        seatPriceTierFk.GetConstraintName().Should().Be("fk_seats_price_tiers_composite");
        seatPriceTierFk.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
    }

    [Fact]
    public void Model_EventPrice_MustUseCompositeRelationships_ForLayoutIntegrity()
    {
        var eventPriceEntity = _model.FindEntityType(typeof(EventPrice))!;

        // 1. EventPrice -> Event composite FK: (event_id, seating_layout_id) -> catalog.events(id, seating_layout_id)
        var eventFk = eventPriceEntity.GetForeignKeys()
            .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(Event));
        eventFk.Should().NotBeNull();
        eventFk!.Properties.Select(p => p.Name).Should().Equal("EventId", "SeatingLayoutId");
        eventFk.PrincipalKey.Properties.Select(p => p.Name).Should().Equal("Id", "SeatingLayoutId");
        eventFk.GetConstraintName().Should().Be("fk_event_prices_events_composite");
        eventFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 2. EventPrice -> PriceTier composite FK: (seating_layout_id, price_tier_id) -> catalog.price_tiers(seating_layout_id, id)
        var tierFk = eventPriceEntity.GetForeignKeys()
            .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(PriceTier));
        tierFk.Should().NotBeNull();
        tierFk!.Properties.Select(p => p.Name).Should().Equal("SeatingLayoutId", "PriceTierId");
        tierFk.PrincipalKey.Properties.Select(p => p.Name).Should().Equal("SeatingLayoutId", "Id");
        tierFk.GetConstraintName().Should().Be("fk_event_prices_price_tiers_composite");
        tierFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
    }

    [Fact]
    public void Model_Money_MustUseNumeric184_And_CurrencyMustUseVarchar3()
    {
        var eventPriceEntity = _model.FindEntityType(typeof(EventPrice))!;
        var priceComplexProperty = eventPriceEntity.GetComplexProperties()
            .FirstOrDefault(cp => cp.Name == nameof(EventPrice.Price));

        priceComplexProperty.Should().NotBeNull("Price must be mapped as a ComplexProperty on EventPrice.");

        var amountProperty = priceComplexProperty!.ComplexType.FindProperty("Amount")!;
        amountProperty.GetColumnName().Should().Be("price_amount");
        amountProperty.GetPrecision().Should().Be(18);
        amountProperty.GetScale().Should().Be(4);

        var currencyProperty = priceComplexProperty.ComplexType.FindProperty("Currency")!;
        currencyProperty.GetColumnName().Should().Be("price_currency");
        currencyProperty.GetMaxLength().Should().Be(3);
    }

    [Fact]
    public void Model_EventStatus_MustBeStoredAsString()
    {
        var eventEntity = _model.FindEntityType(typeof(Event))!;
        var statusProp = eventEntity.FindProperty(nameof(Event.Status))!;

        statusProp.GetColumnName().Should().Be("status");
        statusProp.GetValueConverter().Should().NotBeNull();
        statusProp.GetValueConverter()!.ProviderClrType.Should().Be(typeof(string));
        statusProp.GetMaxLength().Should().Be(30);
    }

    [Fact]
    public void Model_OptimisticConcurrencyTokens_MustExistOnlyForAggregateRoots()
    {
        var venueVersion = _model.FindEntityType(typeof(Venue))!.FindProperty("Version")!;
        venueVersion.IsConcurrencyToken.Should().BeTrue();
        venueVersion.GetColumnName().Should().Be("version");
        venueVersion.GetDefaultValue().Should().Be(1);

        var layoutVersion = _model.FindEntityType(typeof(SeatingLayout))!.FindProperty("Version")!;
        layoutVersion.IsConcurrencyToken.Should().BeTrue();
        layoutVersion.GetColumnName().Should().Be("version");
        layoutVersion.GetDefaultValue().Should().Be(1);

        var eventVersion = _model.FindEntityType(typeof(Event))!.FindProperty("Version")!;
        eventVersion.IsConcurrencyToken.Should().BeTrue();
        eventVersion.GetColumnName().Should().Be("version");
        eventVersion.GetDefaultValue().Should().Be(1);

        // Child entities must NOT have concurrency tokens
        _model.FindEntityType(typeof(Section))!.GetProperties()
            .Any(p => p.IsConcurrencyToken).Should().BeFalse();

        _model.FindEntityType(typeof(Row))!.GetProperties()
            .Any(p => p.IsConcurrencyToken).Should().BeFalse();

        _model.FindEntityType(typeof(Seat))!.GetProperties()
            .Any(p => p.IsConcurrencyToken).Should().BeFalse();

        _model.FindEntityType(typeof(PriceTier))!.GetProperties()
            .Any(p => p.IsConcurrencyToken).Should().BeFalse();

        _model.FindEntityType(typeof(EventPrice))!.GetProperties()
            .Any(p => p.IsConcurrencyToken).Should().BeFalse();
    }

    [Fact]
    public void Model_CatalogSeat_MustNotContain_ReservationStateFields()
    {
        var seatEntity = _model.FindEntityType(typeof(Seat))!;
        var propertyNames = seatEntity.GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();

        propertyNames.Should().NotContain([
            "reservationid",
            "userid",
            "customerid",
            "holdtoken",
            "expiresat",
            "helduntil",
            "status",
            "bookingstatus"
        ]);
    }

    [Fact]
    public void Model_PriceTier_MustNotContain_EventSpecificPriceFields()
    {
        var tierEntity = _model.FindEntityType(typeof(PriceTier))!;
        var propertyNames = tierEntity.GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();

        propertyNames.Should().NotContain([
            "price",
            "money",
            "priceamount",
            "pricecurrency",
            "amount",
            "currency"
        ]);
    }
}
