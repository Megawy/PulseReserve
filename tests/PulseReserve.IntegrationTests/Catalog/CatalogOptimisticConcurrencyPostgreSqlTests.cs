using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.Modules.Catalog.Domain.Venues;
using Xunit;

namespace PulseReserve.IntegrationTests.Catalog;

/// <summary>
/// Proves aggregate-level optimistic concurrency using REAL PostgreSQL database connections and multiple DbContext instances.
/// Tests verify that child entity mutations propagate to the Aggregate Root version token, causing concurrent stale updates
/// to be rejected with DbUpdateConcurrencyException.
/// </summary>
public class CatalogOptimisticConcurrencyPostgreSqlTests : IClassFixture<PostgreSqlDatabaseFixture>
{
    private readonly PostgreSqlDatabaseFixture _fixture;

    public CatalogOptimisticConcurrencyPostgreSqlTests(PostgreSqlDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Scenario A: SeatingLayout child concurrency
    /// Context A loads Version X
    /// Context B loads Version X
    /// A modifies child -> SaveChanges SUCCESS (Version X -> X + 1)
    /// B modifies child -> SaveChanges throws DbUpdateConcurrencyException
    /// </summary>
    [Fact]
    public async Task TestA_SeatingLayout_ChildConcurrency_ThrowsDbUpdateConcurrencyException()
    {
        var venueId = VenueId.New();
        var layoutId = SeatingLayoutId.New();

        // 1. Seed initial data
        using (var seedContext = _fixture.CreateDbContext())
        {
            var venue = Venue.Create(venueId, "Olympic Stadium", Address.Create("Main St", "City", "Country", "12345").Value).Value;
            var layout = SeatingLayout.Create(layoutId, venueId, "Grand Arena").Value;
            var section = Section.Create(SectionId.New(), "North Stand", 0).Value;
            layout.AddSection(section);
            var row = Row.Create(RowId.New(), section.Id, "Row 1", 0).Value;
            layout.AddRow(row);
            var tier = PriceTier.Create(PriceTierId.New(), "Premium Tier").Value;
            layout.AddPriceTier(tier);
            var seatA = Seat.Create(SeatId.New(), row.Id, section.Id, "Seat A", SeatCoordinate.Create(1, 1).Value, tier.Id).Value;
            var seatB = Seat.Create(SeatId.New(), row.Id, section.Id, "Seat B", SeatCoordinate.Create(1, 2).Value, tier.Id).Value;
            layout.AddSeat(seatA);
            layout.AddSeat(seatB);

            seedContext.Venues.Add(venue);
            seedContext.SeatingLayouts.Add(layout);
            await seedContext.SaveChangesAsync();
        }

        // 2. Context A and Context B load the exact same SeatingLayout (both observe version = 1)
        using var contextA = _fixture.CreateDbContext();
        using var contextB = _fixture.CreateDbContext();

        var layoutA = await contextA.SeatingLayouts
            .Include(l => l.Sections)
            .Include(l => l.Rows)
            .Include(l => l.PriceTiers)
            .Include(l => l.Seats)
            .FirstAsync(l => l.Id == layoutId);

        var layoutB = await contextB.SeatingLayouts
            .Include(l => l.Sections)
            .Include(l => l.Rows)
            .Include(l => l.PriceTiers)
            .Include(l => l.Seats)
            .FirstAsync(l => l.Id == layoutId);

        contextA.Entry(layoutA).Property<int>("Version").CurrentValue.Should().Be(1);
        contextB.Entry(layoutB).Property<int>("Version").CurrentValue.Should().Be(1);

        // 3. Context A modifies child Seat A and saves successfully (advancing root version to 2)
        var seatInA = layoutA.Seats.First(s => s.Label == "Seat A");
        contextA.Entry(seatInA).Property(s => s.Label).CurrentValue = "Seat A - Modified by A";
        contextA.Entry(seatInA).State = EntityState.Modified;

        await contextA.SaveChangesAsync(); // SUCCESS! DB version is now 2.

        // 4. Context B modifies child Seat B and attempts to save with stale expected version = 1
        var seatInB = layoutB.Seats.First(s => s.Label == "Seat B");
        contextB.Entry(seatInB).Property(s => s.Label).CurrentValue = "Seat B - Modified by B";
        contextB.Entry(seatInB).State = EntityState.Modified;

        // Context B MUST throw DbUpdateConcurrencyException because WHERE version = 1 matches 0 rows in DB
        var actB = async () => await contextB.SaveChangesAsync();
        var ex = await actB.Should().ThrowAsync<DbUpdateConcurrencyException>();
        ex.Which.Entries.Should().ContainSingle(e => ReferenceEquals(e.Entity, layoutB));
        contextB.Entry(layoutB).Property<int>("Version").OriginalValue.Should().Be(1);

        // 5. Verify database ultimately contains version = 2 and B's write was rejected
        using var verifyContext = _fixture.CreateDbContext();
        var finalLayout = await verifyContext.SeatingLayouts
            .Include(l => l.Seats)
            .FirstAsync(l => l.Id == layoutId);

        verifyContext.Entry(finalLayout).Property<int>("Version").CurrentValue.Should().Be(2);
        finalLayout.Seats.First(s => s.Id == seatInA.Id).Label.Should().Be("Seat A - Modified by A");
        finalLayout.Seats.First(s => s.Id == seatInB.Id).Label.Should().Be("Seat B");
    }

    /// <summary>
    /// Scenario B: SeatingLayout multiple children
    /// Multiple children changed in one DbContext: Seat, Seat, Row, PriceTier
    /// Expected: Version X -> X + 1, not X -> X + 4
    /// </summary>
    [Fact]
    public async Task TestB_SeatingLayout_MultipleChildren_AdvancesVersionOnlyOnce()
    {
        var venueId = VenueId.New();
        var layoutId = SeatingLayoutId.New();
        var sectionId = SectionId.New();
        var rowId = RowId.New();
        var tierId = PriceTierId.New();

        using (var seedContext = _fixture.CreateDbContext())
        {
            var venue = Venue.Create(venueId, "Arena B", Address.Create("St", "City", "Country", "123").Value).Value;
            var layout = SeatingLayout.Create(layoutId, venueId, "Hall B").Value;
            var section = Section.Create(sectionId, "Sec 1", 0).Value;
            layout.AddSection(section);
            var row = Row.Create(rowId, sectionId, "R1", 0).Value;
            layout.AddRow(row);
            var tier = PriceTier.Create(tierId, "T1").Value;
            layout.AddPriceTier(tier);
            var seatA = Seat.Create(SeatId.New(), rowId, sectionId, "S1", SeatCoordinate.Create(1, 1).Value, tierId).Value;
            var seatB = Seat.Create(SeatId.New(), rowId, sectionId, "S2", SeatCoordinate.Create(1, 2).Value, tierId).Value;
            layout.AddSeat(seatA);
            layout.AddSeat(seatB);

            seedContext.Venues.Add(venue);
            seedContext.SeatingLayouts.Add(layout);
            await seedContext.SaveChangesAsync();
        }

        using var context = _fixture.CreateDbContext();
        var layoutLoaded = await context.SeatingLayouts
            .Include(l => l.Sections)
            .Include(l => l.Rows)
            .Include(l => l.PriceTiers)
            .Include(l => l.Seats)
            .FirstAsync(l => l.Id == layoutId);

        context.Entry(layoutLoaded).Property<int>("Version").CurrentValue.Should().Be(1);

        // Mutate 4 children: Seat 1, Seat 2, Row, PriceTier
        var seat1 = layoutLoaded.Seats.First(s => s.Label == "S1");
        var seat2 = layoutLoaded.Seats.First(s => s.Label == "S2");
        var r = layoutLoaded.Rows.First();
        var t = layoutLoaded.PriceTiers.First();

        context.Entry(seat1).Property(s => s.Label).CurrentValue = "S1-Updated";
        context.Entry(seat1).State = EntityState.Modified;

        context.Entry(seat2).Property(s => s.Label).CurrentValue = "S2-Updated";
        context.Entry(seat2).State = EntityState.Modified;

        context.Entry(r).Property(x => x.Label).CurrentValue = "R1-Updated";
        context.Entry(r).State = EntityState.Modified;

        context.Entry(t).Property(x => x.Name).CurrentValue = "T1-Updated";
        context.Entry(t).State = EntityState.Modified;

        await context.SaveChangesAsync();

        // Must advance exactly once: 1 -> 2, NOT 1 -> 5
        using var verifyContext = _fixture.CreateDbContext();
        var finalLayout = await verifyContext.SeatingLayouts.FirstAsync(l => l.Id == layoutId);
        verifyContext.Entry(finalLayout).Property<int>("Version").CurrentValue.Should().Be(2);
    }

    /// <summary>
    /// Scenario C: EventPrice concurrency
    /// Context A loads Event Version X
    /// Context B loads Event Version X
    /// A modifies EventPrice -> SaveChanges SUCCESS
    /// B modifies EventPrice -> SaveChanges throws DbUpdateConcurrencyException
    /// </summary>
    [Fact]
    public async Task TestC_Event_EventPriceConcurrency_ThrowsDbUpdateConcurrencyException()
    {
        var venueId = VenueId.New();
        var layoutId = SeatingLayoutId.New();
        var tierId = PriceTierId.New();
        var eventId = EventId.New();

        // 1. Seed initial Venue, Layout with tier, and Event with EventPrice
        using (var seedContext = _fixture.CreateDbContext())
        {
            var venue = Venue.Create(venueId, "Music Hall C", Address.Create("Street", "City", "Country", "12345").Value).Value;
            var layout = SeatingLayout.Create(layoutId, venueId, "Main Hall C").Value;
            var section = Section.Create(SectionId.New(), "Floor", 0).Value;
            layout.AddSection(section);
            var row = Row.Create(RowId.New(), section.Id, "R1", 0).Value;
            layout.AddRow(row);
            var tier = PriceTier.Create(tierId, "Standard").Value;
            layout.AddPriceTier(tier);
            var seat = Seat.Create(SeatId.New(), row.Id, section.Id, "S1", SeatCoordinate.Create(1, 1).Value, tierId).Value;
            layout.AddSeat(seat);

            var evt = Event.CreateDraft(
                eventId,
                "Symphony Night C",
                "Classical symphony concert",
                venueId,
                DateTime.UtcNow.AddDays(7),
                DateTime.UtcNow.AddDays(7).AddHours(2)).Value;

            evt.AssignSeatingLayout(layoutId);
            evt.SetPrice(tierId, Money.Create(75m, "USD").Value);

            seedContext.Venues.Add(venue);
            seedContext.SeatingLayouts.Add(layout);
            seedContext.Events.Add(evt);
            await seedContext.SaveChangesAsync();
        }

        // 2. Context A and Context B load the same Event (both observe version = 1)
        using var contextA = _fixture.CreateDbContext();
        using var contextB = _fixture.CreateDbContext();

        var eventA = await contextA.Events.Include(e => e.Prices).FirstAsync(e => e.Id == eventId);
        var eventB = await contextB.Events.Include(e => e.Prices).FirstAsync(e => e.Id == eventId);

        contextA.Entry(eventA).Property<int>("Version").CurrentValue.Should().Be(1);
        contextB.Entry(eventB).Property<int>("Version").CurrentValue.Should().Be(1);

        // 3. Context A modifies price to 85 USD and saves successfully
        var priceInA = eventA.Prices.First();
        contextA.Entry(priceInA).ComplexProperty(p => p.Price).Property(m => m.Amount).CurrentValue = 85m;
        contextA.Entry(priceInA).State = EntityState.Modified;
        await contextA.SaveChangesAsync(); // SUCCESS! Version = 2 in DB

        // 4. Context B modifies price to 95 USD and attempts to save with stale version = 1
        var priceInB = eventB.Prices.First();
        contextB.Entry(priceInB).ComplexProperty(p => p.Price).Property(m => m.Amount).CurrentValue = 95m;
        contextB.Entry(priceInB).State = EntityState.Modified;

        var actB = async () => await contextB.SaveChangesAsync();
        var ex = await actB.Should().ThrowAsync<DbUpdateConcurrencyException>();
        ex.Which.Entries.Should().ContainSingle(e => ReferenceEquals(e.Entity, eventB));
        contextB.Entry(eventB).Property<int>("Version").OriginalValue.Should().Be(1);

        // 5. Verify database version = 2 and price = 85
        using var verifyContext = _fixture.CreateDbContext();
        var finalEvent = await verifyContext.Events.Include(e => e.Prices).FirstAsync(e => e.Id == eventId);
        verifyContext.Entry(finalEvent).Property<int>("Version").CurrentValue.Should().Be(2);
        finalEvent.Prices.First().Price.Amount.Should().Be(85m);
    }

    /// <summary>
    /// Scenario D: Event root concurrency
    /// Two contexts load the same Event version.
    /// First root mutation succeeds.
    /// Second stale mutation must throw DbUpdateConcurrencyException.
    /// </summary>
    [Fact]
    public async Task TestD_Event_RootConcurrency_ThrowsDbUpdateConcurrencyException()
    {
        var venueId = VenueId.New();
        var layoutId = SeatingLayoutId.New();
        var eventId = EventId.New();

        using (var seedContext = _fixture.CreateDbContext())
        {
            var venue = Venue.Create(venueId, "Venue D", Address.Create("Street", "City", "Country", "123").Value).Value;
            var layout = SeatingLayout.Create(layoutId, venueId, "Layout D").Value;
            var evt = Event.CreateDraft(
                eventId,
                "Event D",
                "Description D",
                venueId,
                DateTime.UtcNow.AddDays(5),
                DateTime.UtcNow.AddDays(5).AddHours(3)).Value;
            evt.AssignSeatingLayout(layoutId);

            seedContext.Venues.Add(venue);
            seedContext.SeatingLayouts.Add(layout);
            seedContext.Events.Add(evt);
            await seedContext.SaveChangesAsync();
        }

        using var contextA = _fixture.CreateDbContext();
        using var contextB = _fixture.CreateDbContext();

        var eventA = await contextA.Events.FirstAsync(e => e.Id == eventId);
        var eventB = await contextB.Events.FirstAsync(e => e.Id == eventId);

        contextA.Entry(eventA).Property<int>("Version").CurrentValue.Should().Be(1);
        contextB.Entry(eventB).Property<int>("Version").CurrentValue.Should().Be(1);

        // Context A publishes the event and saves -> SUCCESS (Version 1 -> 2)
        eventA.Publish(DateTime.UtcNow);
        contextA.Entry(eventA).State = EntityState.Modified;
        await contextA.SaveChangesAsync();

        // Context B modifies description with stale expected version 1
        contextB.Entry(eventB).Property(e => e.Description).CurrentValue = "Updated by Context B";
        contextB.Entry(eventB).State = EntityState.Modified;

        var actB = async () => await contextB.SaveChangesAsync();
        var ex = await actB.Should().ThrowAsync<DbUpdateConcurrencyException>();
        ex.Which.Entries.Should().ContainSingle(e => ReferenceEquals(e.Entity, eventB));
        contextB.Entry(eventB).Property<int>("Version").OriginalValue.Should().Be(1);

        // Verify database state: status is Published, description is unchanged, version is 2
        using var verifyContext = _fixture.CreateDbContext();
        var finalEvent = await verifyContext.Events.FirstAsync(e => e.Id == eventId);
        verifyContext.Entry(finalEvent).Property<int>("Version").CurrentValue.Should().Be(2);
        finalEvent.Status.Should().Be(EventStatus.Published);
        finalEvent.Description.Should().Be("Description D");
    }

    /// <summary>
    /// Scenario E: Different aggregates
    /// Two independent aggregate roots are modified concurrently in separate DbContexts.
    /// Both must succeed when there is no shared aggregate boundary.
    /// </summary>
    [Fact]
    public async Task TestE_DifferentAggregates_ConcurrentModifications_BothSucceedWithoutCollision()
    {
        var venueId = VenueId.New();
        var layoutId1 = SeatingLayoutId.New();
        var layoutId2 = SeatingLayoutId.New();

        using (var seedContext = _fixture.CreateDbContext())
        {
            var venue = Venue.Create(venueId, "Venue E", Address.Create("Street", "City", "Country", "123").Value).Value;
            var layout1 = SeatingLayout.Create(layoutId1, venueId, "Layout 1").Value;
            var layout2 = SeatingLayout.Create(layoutId2, venueId, "Layout 2").Value;

            seedContext.Venues.Add(venue);
            seedContext.SeatingLayouts.AddRange(layout1, layout2);
            await seedContext.SaveChangesAsync();
        }

        using var context1 = _fixture.CreateDbContext();
        using var context2 = _fixture.CreateDbContext();

        var loaded1 = await context1.SeatingLayouts.FirstAsync(l => l.Id == layoutId1);
        var loaded2 = await context2.SeatingLayouts.FirstAsync(l => l.Id == layoutId2);

        // Modify Layout 1 in context 1
        context1.Entry(loaded1).Property(l => l.Name).CurrentValue = "Layout 1 - Renamed";
        context1.Entry(loaded1).State = EntityState.Modified;

        // Modify Layout 2 in context 2
        context2.Entry(loaded2).Property(l => l.Name).CurrentValue = "Layout 2 - Renamed";
        context2.Entry(loaded2).State = EntityState.Modified;

        // Both SaveChanges must succeed independently without collision
        await context1.SaveChangesAsync();
        await context2.SaveChangesAsync();

        // Verify both reached version 2 and persisted changes
        using var verifyContext = _fixture.CreateDbContext();
        var v1 = await verifyContext.SeatingLayouts.FirstAsync(l => l.Id == layoutId1);
        var v2 = await verifyContext.SeatingLayouts.FirstAsync(l => l.Id == layoutId2);

        verifyContext.Entry(v1).Property<int>("Version").CurrentValue.Should().Be(2);
        verifyContext.Entry(v2).Property<int>("Version").CurrentValue.Should().Be(2);
        v1.Name.Should().Be("Layout 1 - Renamed");
        v2.Name.Should().Be("Layout 2 - Renamed");
    }

    /// <summary>
    /// Scenario F: Stale aggregate version
    /// A stale root update must be rejected deterministically.
    /// Explicitly proves the predicate WHERE id = @id AND version = @originalVersion.
    /// </summary>
    [Fact]
    public async Task TestF_StaleAggregateVersion_RejectedDeterministically()
    {
        var venueId = VenueId.New();
        var layoutId = SeatingLayoutId.New();

        using (var seedContext = _fixture.CreateDbContext())
        {
            var venue = Venue.Create(venueId, "Venue F", Address.Create("Street", "City", "Country", "123").Value).Value;
            var layout = SeatingLayout.Create(layoutId, venueId, "Hall F").Value;
            seedContext.Venues.Add(venue);
            seedContext.SeatingLayouts.Add(layout);
            await seedContext.SaveChangesAsync(); // Version is 1
        }

        // Simulate an external writer advancing the version in the database to 2
        using (var externalContext = _fixture.CreateDbContext())
        {
            var layoutToAdvance = await externalContext.SeatingLayouts.FirstAsync(l => l.Id == layoutId);
            layoutToAdvance.LockLayout();
            externalContext.Entry(layoutToAdvance).State = EntityState.Modified;
            await externalContext.SaveChangesAsync(); // Version in DB is now 2
        }

        // Now context attempts an update using a stale expected version = 1
        using var staleContext = _fixture.CreateDbContext();
        var staleLayout = await staleContext.SeatingLayouts.FirstAsync(l => l.Id == layoutId);

        // Manually set OriginalValue of Version to 1 (representing a cached / stale entity)
        staleContext.Entry(staleLayout).Property<int>("Version").OriginalValue = 1;
        staleContext.Entry(staleLayout).Property(l => l.Name).CurrentValue = "Stale Update Attempt";
        staleContext.Entry(staleLayout).State = EntityState.Modified;

        var act = async () => await staleContext.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        ex.Which.Entries.Should().ContainSingle(e => ReferenceEquals(e.Entity, staleLayout));

        // Verify the database was not overwritten
        using var verifyContext = _fixture.CreateDbContext();
        var finalLayout = await verifyContext.SeatingLayouts.FirstAsync(l => l.Id == layoutId);
        verifyContext.Entry(finalLayout).Property<int>("Version").CurrentValue.Should().Be(2);
        finalLayout.Name.Should().Be("Hall F");
    }
}
