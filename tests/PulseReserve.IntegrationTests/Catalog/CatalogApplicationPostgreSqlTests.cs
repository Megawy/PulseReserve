using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PulseReserve.Infrastructure.Persistence.Catalog.Repositories;
using PulseReserve.Infrastructure;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.Modules.Catalog.Domain.Venues;
using PulseReserve.Modules.Catalog.Features.Events.CreateEvent;
using PulseReserve.Modules.Catalog.Features.Events.PublishEvent;
using PulseReserve.Modules.Catalog.Features.Events.SetEventPrice;
using PulseReserve.Modules.Catalog.Features.Events.StartSale;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddPriceTier;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddRow;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSeat;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSection;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.CreateSeatingLayout;
using PulseReserve.Modules.Catalog.Features.Venues.CreateVenue;
using Xunit;

namespace PulseReserve.IntegrationTests.Catalog;

public class CatalogApplicationPostgreSqlTests : IClassFixture<PostgreSqlDatabaseFixture>
{
    private readonly PostgreSqlDatabaseFixture _fixture;

    public CatalogApplicationPostgreSqlTests(PostgreSqlDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EndToEnd_Lifecycle_Venue_Layout_Event_Publish_StartSale_PersistsCleanly()
    {
        var venueId = VenueId.New();
        var layoutId = SeatingLayoutId.New();
        var sectionId = SectionId.New();
        var rowId = RowId.New();
        var tierId = PriceTierId.New();
        var seatId = SeatId.New();
        var eventId = EventId.New();

        // 1. Create Venue
        using (var context = _fixture.CreateDbContext())
        {
            var venueRepo = new VenueRepository(context);
            var uow = new CatalogUnitOfWork(context);
            var handler = new CreateVenueHandler(venueRepo, uow);

            var result = await handler.HandleAsync(new CreateVenueCommand(
                venueId,
                "Royal Albert Hall",
                "Kensington Gore",
                "London",
                "United Kingdom",
                "SW7 2AP"));

            result.IsSuccess.Should().BeTrue();
        }

        // 2. Create SeatingLayout
        using (var context = _fixture.CreateDbContext())
        {
            var venueRepo = new VenueRepository(context);
            var layoutRepo = new SeatingLayoutRepository(context);
            var uow = new CatalogUnitOfWork(context);
            var handler = new CreateSeatingLayoutHandler(venueRepo, layoutRepo, uow);

            var result = await handler.HandleAsync(new CreateSeatingLayoutCommand(
                layoutId,
                venueId,
                "Proms Layout"));

            result.IsSuccess.Should().BeTrue();
        }

        // 3. Build Layout: Add Section, Row, Tier, and Seat
        using (var context = _fixture.CreateDbContext())
        {
            var layoutRepo = new SeatingLayoutRepository(context);
            var uow = new CatalogUnitOfWork(context);

            var addSec = new AddSectionHandler(layoutRepo, uow);
            var secResult = await addSec.HandleAsync(new AddSectionCommand(layoutId, sectionId, "Stalls", 1));
            secResult.IsSuccess.Should().BeTrue();

            var addRow = new AddRowHandler(layoutRepo, uow);
            var rowResult = await addRow.HandleAsync(new AddRowCommand(layoutId, rowId, sectionId, "Row A", 1));
            rowResult.IsSuccess.Should().BeTrue();

            var addTier = new AddPriceTierHandler(layoutRepo, uow);
            var tierResult = await addTier.HandleAsync(new AddPriceTierCommand(layoutId, tierId, "Standard Admission"));
            tierResult.IsSuccess.Should().BeTrue();

            var addSeat = new AddSeatHandler(layoutRepo, uow);
            var seatResult = await addSeat.HandleAsync(new AddSeatCommand(
                layoutId,
                seatId,
                rowId,
                sectionId,
                "Seat 1",
                1,
                1,
                tierId));
            seatResult.IsSuccess.Should().BeTrue();
        }

        // 4. Create Event
        using (var context = _fixture.CreateDbContext())
        {
            var venueRepo = new VenueRepository(context);
            var layoutRepo = new SeatingLayoutRepository(context);
            var eventRepo = new EventRepository(context);
            var uow = new CatalogUnitOfWork(context);
            var handler = new CreateEventHandler(venueRepo, layoutRepo, eventRepo, uow);

            var startUtc = DateTime.UtcNow.AddDays(30);
            var endUtc = startUtc.AddHours(3);
            var result = await handler.HandleAsync(new CreateEventCommand(
                eventId,
                "BBC Proms 2026",
                "Grand Classical Concert",
                venueId,
                startUtc,
                endUtc,
                layoutId));

            result.IsSuccess.Should().BeTrue();
        }

        // 5. Set Event Price
        using (var context = _fixture.CreateDbContext())
        {
            var eventRepo = new EventRepository(context);
            var uow = new CatalogUnitOfWork(context);
            var handler = new SetEventPriceHandler(eventRepo, uow);

            var result = await handler.HandleAsync(new SetEventPriceCommand(
                eventId,
                tierId,
                85.00m,
                "GBP"));

            result.IsSuccess.Should().BeTrue();
        }

        // 6. Publish Event
        using (var context = _fixture.CreateDbContext())
        {
            var eventRepo = new EventRepository(context);
            var layoutRepo = new SeatingLayoutRepository(context);
            var uow = new CatalogUnitOfWork(context);
            var dateTimeProvider = new SystemDateTimeProvider();
            var handler = new PublishEventHandler(eventRepo, layoutRepo, uow, dateTimeProvider);

            var result = await handler.HandleAsync(new PublishEventCommand(eventId));
            result.IsSuccess.Should().BeTrue();
        }

        // 7. Start Sale (atomic multi-aggregate: locks layout and sets event to OnSale)
        using (var context = _fixture.CreateDbContext())
        {
            var eventRepo = new EventRepository(context);
            var layoutRepo = new SeatingLayoutRepository(context);
            var uow = new CatalogUnitOfWork(context);
            var dateTimeProvider = new SystemDateTimeProvider();
            var handler = new StartSaleHandler(eventRepo, layoutRepo, uow, dateTimeProvider);

            var result = await handler.HandleAsync(new StartSaleCommand(eventId));
            result.IsSuccess.Should().BeTrue();
        }

        // 8. Direct database verification using a fresh DbContext
        using (var verifyContext = _fixture.CreateDbContext())
        {
            var venue = await verifyContext.Venues.FirstAsync(v => v.Id == venueId);
            venue.Name.Should().Be("Royal Albert Hall");
            venue.Address.City.Should().Be("London");

            var layout = await verifyContext.SeatingLayouts
                .Include(l => l.Sections)
                .Include(l => l.Rows)
                .Include(l => l.Seats)
                .Include(l => l.PriceTiers)
                .FirstAsync(l => l.Id == layoutId);

            layout.IsLocked.Should().BeTrue();
            layout.Sections.Should().ContainSingle(s => s.Id == sectionId);
            layout.Rows.Should().ContainSingle(r => r.Id == rowId);
            layout.Seats.Should().ContainSingle(s => s.Id == seatId);
            layout.PriceTiers.Should().ContainSingle(t => t.Id == tierId);

            var evt = await verifyContext.Events
                .Include(e => e.Prices)
                .FirstAsync(e => e.Id == eventId);

            evt.Status.Should().Be(EventStatus.OnSale);
            evt.PublishedAtUtc.Should().NotBeNull();
            evt.OnSaleAtUtc.Should().NotBeNull();
            evt.Prices.Should().ContainSingle(p => p.PriceTierId == tierId && p.Price.Amount == 85.00m && p.Price.Currency == "GBP");
        }
    }

    [Fact]
    public async Task MultiAggregate_AtomicityRollback_WhenWorkflowFails_NoPartialChangesPersisted()
    {
        var venueId = VenueId.New();
        var layoutId = SeatingLayoutId.New();
        var sectionId = SectionId.New();
        var rowId = RowId.New();
        var tierId = PriceTierId.New();
        var seatId = SeatId.New();
        var eventId = EventId.New();

        // Seed Venue, valid complete layout (IsLocked = false), and Event in Published state
        using (var seedContext = _fixture.CreateDbContext())
        {
            var venue = Venue.Create(venueId, "Venue Atomicity", Address.Create("Street", "City", "Country", "123").Value).Value;
            var layout = SeatingLayout.Create(layoutId, venueId, "Layout Atomicity").Value;
            layout.AddSection(Section.Create(sectionId, "Sec", 1).Value);
            layout.AddRow(Row.Create(rowId, sectionId, "Row", 1).Value);
            layout.AddPriceTier(PriceTier.Create(tierId, "Tier").Value);
            layout.AddSeat(Seat.Create(seatId, rowId, sectionId, "S1", SeatCoordinate.Create(1, 1).Value, tierId).Value);

            var evt = Event.CreateDraft(eventId, "Atomicity Event", "Desc", venueId, DateTime.UtcNow.AddDays(5), DateTime.UtcNow.AddDays(5).AddHours(2)).Value;
            evt.AssignSeatingLayout(layoutId);
            evt.SetPrice(tierId, Money.Create(50m, "USD").Value);
            evt.Publish(DateTime.UtcNow.AddDays(-1));

            seedContext.Venues.Add(venue);
            seedContext.SeatingLayouts.Add(layout);
            seedContext.Events.Add(evt);
            await seedContext.SaveChangesAsync();
        }

        // Verify initial state: layout is NOT locked, event is Published
        using (var checkContext = _fixture.CreateDbContext())
        {
            var l = await checkContext.SeatingLayouts.FirstAsync(x => x.Id == layoutId);
            l.IsLocked.Should().BeFalse();
            var e = await checkContext.Events.FirstAsync(x => x.Id == eventId);
            e.Status.Should().Be(EventStatus.Published);
        }

        // Attempt StartSale with stale ExpectedLayoutVersion = 999 (causes DbUpdateConcurrencyException upon SaveChanges)
        using (var failContext = _fixture.CreateDbContext())
        {
            var eventRepo = new EventRepository(failContext);
            var layoutRepo = new SeatingLayoutRepository(failContext);
            var uow = new CatalogUnitOfWork(failContext);
            var dateTimeProvider = new SystemDateTimeProvider();
            var handler = new StartSaleHandler(eventRepo, layoutRepo, uow, dateTimeProvider);

            var act = async () => await handler.HandleAsync(new StartSaleCommand(
                eventId,
                ExpectedLayoutVersion: 999));

            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }

        // Verify that in PostgreSQL, neither aggregate was modified (atomic rollback):
        using (var verifyContext = _fixture.CreateDbContext())
        {
            var layoutInDb = await verifyContext.SeatingLayouts.FirstAsync(x => x.Id == layoutId);
            layoutInDb.IsLocked.Should().BeFalse("Layout must not be locked when transaction fails.");

            var eventInDb = await verifyContext.Events.FirstAsync(x => x.Id == eventId);
            eventInDb.Status.Should().Be(EventStatus.Published, "Event must remain in Published state when transaction fails.");
            eventInDb.OnSaleAtUtc.Should().BeNull();
        }
    }

    [Fact]
    public async Task StaleExpectedVersion_InApplicationFeature_ThrowsDbUpdateConcurrencyException()
    {
        var venueId = VenueId.New();
        var layoutId = SeatingLayoutId.New();

        // 1. Seed initial layout (Version = 1)
        using (var seedContext = _fixture.CreateDbContext())
        {
            var venue = Venue.Create(venueId, "Venue Concurrency", Address.Create("Street", "City", "Country", "123").Value).Value;
            var layout = SeatingLayout.Create(layoutId, venueId, "Layout Concurrency").Value;
            seedContext.Venues.Add(venue);
            seedContext.SeatingLayouts.Add(layout);
            await seedContext.SaveChangesAsync();
        }

        // 2. Advance version to 2 via an external context
        using (var advanceContext = _fixture.CreateDbContext())
        {
            var layoutRepo = new SeatingLayoutRepository(advanceContext);
            var uow = new CatalogUnitOfWork(advanceContext);
            var addSection = new AddSectionHandler(layoutRepo, uow);
            var res = await addSection.HandleAsync(new AddSectionCommand(layoutId, SectionId.New(), "Initial Section", 1));
            res.IsSuccess.Should().BeTrue();
        }

        // 3. Attempt to add another section with stale ExpectedVersion = 1
        using (var staleContext = _fixture.CreateDbContext())
        {
            var layoutRepo = new SeatingLayoutRepository(staleContext);
            var uow = new CatalogUnitOfWork(staleContext);
            var addSection = new AddSectionHandler(layoutRepo, uow);

            var act = async () => await addSection.HandleAsync(new AddSectionCommand(
                layoutId,
                SectionId.New(),
                "Stale Section",
                2,
                ExpectedVersion: 1)); // Stale: DB is at 2!

            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }

        // 4. Verify in PostgreSQL that the stale section was NOT added
        using (var verifyContext = _fixture.CreateDbContext())
        {
            var layout = await verifyContext.SeatingLayouts
                .Include(l => l.Sections)
                .FirstAsync(l => l.Id == layoutId);

            layout.Sections.Should().ContainSingle(s => s.Name == "Initial Section");
            layout.Sections.Should().NotContain(s => s.Name == "Stale Section");
        }
    }
}
