using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PulseReserve.Infrastructure.Persistence.Catalog;
using PulseReserve.Infrastructure.Persistence.Catalog.Interceptors;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.Modules.Catalog.Domain.Venues;
using Xunit;

namespace PulseReserve.ArchitectureTests.Catalog;

public class CatalogOptimisticConcurrencyInterceptorTests
{
    private static CatalogDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=test_catalog_db")
            .Options;

        return new CatalogDbContext(options);
    }

    [Fact]
    public void Test1_ChildMutation_Advances_SeatingLayout_RootVersion()
    {
        using var context = CreateContext();
        var venue = Venue.Create(VenueId.New(), "Main Arena", Address.Create("Street", "City", "Country", "12345").Value).Value;
        var layout = SeatingLayout.Create(SeatingLayoutId.New(), venue.Id, "Standard Hall").Value;
        var section = Section.Create(SectionId.New(), "Balcony", 0).Value;
        layout.AddSection(section);
        var row = Row.Create(RowId.New(), section.Id, "A", 0).Value;
        layout.AddRow(row);
        var tier = PriceTier.Create(PriceTierId.New(), "VIP").Value;
        layout.AddPriceTier(tier);
        var seat = Seat.Create(SeatId.New(), row.Id, section.Id, "1", SeatCoordinate.Create(1, 1).Value, tier.Id).Value;
        layout.AddSeat(seat);

        context.Attach(layout);
        context.Entry(layout).Property("Version").CurrentValue = 1;
        context.Entry(layout).Property("Version").OriginalValue = 1;

        // Act: modify a child seat
        context.Entry(seat).Property(s => s.Label).CurrentValue = "Updated-1";
        context.Entry(seat).State = EntityState.Modified;

        CatalogOptimisticConcurrencyInterceptor.AdvanceAggregateVersions(context);

        // Assert: Root version advanced from 1 to 2
        context.Entry(layout).Property("Version").CurrentValue.Should().Be(2);
        context.Entry(layout).Property("Version").OriginalValue.Should().Be(1);
        context.Entry(layout).Property("Version").IsModified.Should().BeTrue();
        context.Entry(layout).State.Should().Be(EntityState.Modified);
    }

    [Fact]
    public void Test2_MultipleChildMutations_WithinSameAggregate_IncrementsVersionOnlyOnce()
    {
        using var context = CreateContext();
        var venue = Venue.Create(VenueId.New(), "Main Arena", Address.Create("Street", "City", "Country", "12345").Value).Value;
        var layout = SeatingLayout.Create(SeatingLayoutId.New(), venue.Id, "Standard Hall").Value;
        var section = Section.Create(SectionId.New(), "Balcony", 0).Value;
        layout.AddSection(section);
        var row = Row.Create(RowId.New(), section.Id, "A", 0).Value;
        layout.AddRow(row);
        var tier = PriceTier.Create(PriceTierId.New(), "VIP").Value;
        layout.AddPriceTier(tier);
        var seatA = Seat.Create(SeatId.New(), row.Id, section.Id, "1", SeatCoordinate.Create(1, 1).Value, tier.Id).Value;
        var seatB = Seat.Create(SeatId.New(), row.Id, section.Id, "2", SeatCoordinate.Create(1, 2).Value, tier.Id).Value;
        layout.AddSeat(seatA);
        layout.AddSeat(seatB);

        context.Attach(layout);
        context.Entry(layout).Property("Version").CurrentValue = 1;
        context.Entry(layout).Property("Version").OriginalValue = 1;

        // Act: modify multiple children (Seat A, Seat B, and Row)
        context.Entry(seatA).Property(s => s.Label).CurrentValue = "A-Updated";
        context.Entry(seatA).State = EntityState.Modified;

        context.Entry(seatB).Property(s => s.Label).CurrentValue = "B-Updated";
        context.Entry(seatB).State = EntityState.Modified;

        context.Entry(row).Property(r => r.Label).CurrentValue = "Row-Updated";
        context.Entry(row).State = EntityState.Modified;

        CatalogOptimisticConcurrencyInterceptor.AdvanceAggregateVersions(context);

        // Assert: Exactly one version increment (1 -> 2, NOT 1 -> 4)
        context.Entry(layout).Property("Version").CurrentValue.Should().Be(2);
        context.Entry(layout).Property("Version").OriginalValue.Should().Be(1);
        context.Entry(layout).Property("Version").IsModified.Should().BeTrue();
    }

    [Fact]
    public void Test3_EventPriceMutation_Advances_Event_RootVersion()
    {
        using var context = CreateContext();
        var eventId = EventId.New();
        var venueId = VenueId.New();
        var layoutId = SeatingLayoutId.New();
        var tierId = PriceTierId.New();

        var evt = Event.CreateDraft(
            eventId,
            "Rock Fest",
            "Annual rock concert",
            venueId,
            DateTime.UtcNow.AddDays(10),
            DateTime.UtcNow.AddDays(10).AddHours(3)).Value;

        evt.AssignSeatingLayout(layoutId);
        evt.SetPrice(tierId, Money.Create(100m, "USD").Value);

        context.Attach(evt);
        context.Entry(evt).Property("Version").CurrentValue = 1;
        context.Entry(evt).Property("Version").OriginalValue = 1;

        // Act: modify event price
        var price = evt.Prices.First();
        context.Entry(price).ComplexProperty(p => p.Price).Property(m => m.Amount).CurrentValue = 150m;
        context.Entry(price).State = EntityState.Modified;

        CatalogOptimisticConcurrencyInterceptor.AdvanceAggregateVersions(context);

        // Assert: Event root version advanced from 1 to 2
        context.Entry(evt).Property("Version").CurrentValue.Should().Be(2);
        context.Entry(evt).Property("Version").OriginalValue.Should().Be(1);
        context.Entry(evt).Property("Version").IsModified.Should().BeTrue();
        context.Entry(evt).State.Should().Be(EntityState.Modified);
    }

    [Fact]
    public void Test6_DirectRootMutation_AdvancesVersionOnce()
    {
        using var context = CreateContext();
        var venue = Venue.Create(VenueId.New(), "Main Arena", Address.Create("Street", "City", "Country", "12345").Value).Value;
        var layout = SeatingLayout.Create(SeatingLayoutId.New(), venue.Id, "Standard Hall").Value;

        context.Attach(layout);
        context.Entry(layout).Property("Version").CurrentValue = 5;
        context.Entry(layout).Property("Version").OriginalValue = 5;

        // Act: Direct root mutation (LockLayout)
        layout.LockLayout();
        context.Entry(layout).State = EntityState.Modified;

        CatalogOptimisticConcurrencyInterceptor.AdvanceAggregateVersions(context);

        // Assert: Advances exactly once (5 -> 6, not 5 -> 7)
        context.Entry(layout).Property("Version").CurrentValue.Should().Be(6);
        context.Entry(layout).Property("Version").OriginalValue.Should().Be(5);
        context.Entry(layout).Property("Version").IsModified.Should().BeTrue();
    }

    [Fact]
    public void Test7_RootAndChildMutation_Combined_AdvancesVersionOnce()
    {
        using var context = CreateContext();
        var venue = Venue.Create(VenueId.New(), "Main Arena", Address.Create("Street", "City", "Country", "12345").Value).Value;
        var layout = SeatingLayout.Create(SeatingLayoutId.New(), venue.Id, "Standard Hall").Value;
        var section = Section.Create(SectionId.New(), "Balcony", 0).Value;
        layout.AddSection(section);
        var row = Row.Create(RowId.New(), section.Id, "A", 0).Value;
        layout.AddRow(row);
        var tier = PriceTier.Create(PriceTierId.New(), "VIP").Value;
        layout.AddPriceTier(tier);
        var seat = Seat.Create(SeatId.New(), row.Id, section.Id, "1", SeatCoordinate.Create(1, 1).Value, tier.Id).Value;
        layout.AddSeat(seat);

        context.Attach(layout);
        context.Entry(layout).Property("Version").CurrentValue = 3;
        context.Entry(layout).Property("Version").OriginalValue = 3;

        // Act: Direct root mutation AND child mutation
        layout.LockLayout();
        context.Entry(layout).State = EntityState.Modified;

        context.Entry(seat).Property(s => s.Label).CurrentValue = "Updated-Seat";
        context.Entry(seat).State = EntityState.Modified;

        CatalogOptimisticConcurrencyInterceptor.AdvanceAggregateVersions(context);

        // Assert: Combined mutations advance version exactly once (3 -> 4, NOT 3 -> 5)
        context.Entry(layout).Property("Version").CurrentValue.Should().Be(4);
        context.Entry(layout).Property("Version").OriginalValue.Should().Be(3);
        context.Entry(layout).Property("Version").IsModified.Should().BeTrue();
    }

    [Fact]
    public void DirectVenueMutation_AdvancesVersionOnce()
    {
        using var context = CreateContext();
        var venue = Venue.Create(VenueId.New(), "Original Name", Address.Create("Street", "City", "Country", "12345").Value).Value;

        context.Attach(venue);
        context.Entry(venue).Property("Version").CurrentValue = 1;
        context.Entry(venue).Property("Version").OriginalValue = 1;

        // Act: Direct venue modification
        context.Entry(venue).Property(v => v.Name).CurrentValue = "New Arena Name";
        context.Entry(venue).State = EntityState.Modified;

        CatalogOptimisticConcurrencyInterceptor.AdvanceAggregateVersions(context);

        // Assert: Venue version advanced 1 -> 2
        context.Entry(venue).Property("Version").CurrentValue.Should().Be(2);
        context.Entry(venue).Property("Version").OriginalValue.Should().Be(1);
        context.Entry(venue).Property("Version").IsModified.Should().BeTrue();
    }

    [Fact]
    public void MultipleAggregates_InOneSaveChanges_AdvanceIndependently()
    {
        using var context = CreateContext();
        var venue = Venue.Create(VenueId.New(), "Arena", Address.Create("S", "C", "C", "1").Value).Value;
        var layoutA = SeatingLayout.Create(SeatingLayoutId.New(), venue.Id, "Layout A").Value;
        var sectionA = Section.Create(SectionId.New(), "Sec A", 0).Value;
        layoutA.AddSection(sectionA);
        var rowA = Row.Create(RowId.New(), sectionA.Id, "Row A", 0).Value;
        layoutA.AddRow(rowA);
        var tierA = PriceTier.Create(PriceTierId.New(), "Tier A").Value;
        layoutA.AddPriceTier(tierA);
        var seatA = Seat.Create(SeatId.New(), rowA.Id, sectionA.Id, "1", SeatCoordinate.Create(1, 1).Value, tierA.Id).Value;
        layoutA.AddSeat(seatA);

        var evtB = Event.CreateDraft(
            EventId.New(),
            "Event B",
            "Desc",
            venue.Id,
            DateTime.UtcNow.AddDays(5),
            DateTime.UtcNow.AddDays(5).AddHours(2)).Value;

        evtB.AssignSeatingLayout(layoutA.Id);
        evtB.SetPrice(tierA.Id, Money.Create(50m, "USD").Value);

        context.Attach(layoutA);
        context.Attach(evtB);

        context.Entry(layoutA).Property("Version").CurrentValue = 10;
        context.Entry(layoutA).Property("Version").OriginalValue = 10;

        context.Entry(evtB).Property("Version").CurrentValue = 20;
        context.Entry(evtB).Property("Version").OriginalValue = 20;

        // Act: modify child in Layout A and child in Event B
        context.Entry(seatA).Property(s => s.Label).CurrentValue = "New A";
        context.Entry(seatA).State = EntityState.Modified;

        var price = evtB.Prices.First();
        context.Entry(price).ComplexProperty(p => p.Price).Property(m => m.Amount).CurrentValue = 75m;
        context.Entry(price).State = EntityState.Modified;

        CatalogOptimisticConcurrencyInterceptor.AdvanceAggregateVersions(context);

        // Assert: Each aggregate advances independently
        context.Entry(layoutA).Property("Version").CurrentValue.Should().Be(11);
        context.Entry(layoutA).Property("Version").OriginalValue.Should().Be(10);

        context.Entry(evtB).Property("Version").CurrentValue.Should().Be(21);
        context.Entry(evtB).Property("Version").OriginalValue.Should().Be(20);
    }

    [Fact]
    public void ChildMutation_WithoutTrackedRoot_ThrowsInvalidOperationException()
    {
        using var context = CreateContext();
        var layoutId = SeatingLayoutId.New();
        var section = Section.Create(SectionId.New(), "Balcony", 0).Value;

        var entry = context.Entry(section);
        entry.Property("SeatingLayoutId").CurrentValue = layoutId;
        entry.Property(s => s.Name).CurrentValue = "Updated Balcony";
        entry.State = EntityState.Modified;

        // Act & Assert
        var act = () => CatalogOptimisticConcurrencyInterceptor.AdvanceAggregateVersions(context);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*The aggregate root SeatingLayout with ID '{layoutId}' is not tracked in the current DbContext*");
    }

    [Fact]
    public void Interceptor_Idempotency_CallingSavingChangesTwice_DoesNotDoubleIncrement()
    {
        using var context = CreateContext();
        var venue = Venue.Create(VenueId.New(), "Main Arena", Address.Create("Street", "City", "Country", "12345").Value).Value;
        var layout = SeatingLayout.Create(SeatingLayoutId.New(), venue.Id, "Standard Hall").Value;
        var section = Section.Create(SectionId.New(), "Balcony", 0).Value;
        layout.AddSection(section);
        var row = Row.Create(RowId.New(), section.Id, "A", 0).Value;
        layout.AddRow(row);
        var tier = PriceTier.Create(PriceTierId.New(), "VIP").Value;
        layout.AddPriceTier(tier);
        var seat = Seat.Create(SeatId.New(), row.Id, section.Id, "1", SeatCoordinate.Create(1, 1).Value, tier.Id).Value;
        layout.AddSeat(seat);

        context.Attach(layout);
        context.Entry(layout).Property("Version").CurrentValue = 1;
        context.Entry(layout).Property("Version").OriginalValue = 1;

        context.Entry(seat).Property(s => s.Label).CurrentValue = "Updated-1";
        context.Entry(seat).State = EntityState.Modified;

        // Act: invoke AdvanceAggregateVersions twice
        CatalogOptimisticConcurrencyInterceptor.AdvanceAggregateVersions(context);
        CatalogOptimisticConcurrencyInterceptor.AdvanceAggregateVersions(context);

        // Assert: Version is still 2, not 3
        context.Entry(layout).Property("Version").CurrentValue.Should().Be(2);
        context.Entry(layout).Property("Version").OriginalValue.Should().Be(1);
    }
}
