using FluentAssertions;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.Modules.Catalog.Domain.Venues;
using PulseReserve.Modules.Catalog.Features.Events.CreateEvent;
using PulseReserve.Modules.Catalog.Features.Events.GetEventById;
using PulseReserve.Modules.Catalog.Features.Events.PublishEvent;
using PulseReserve.Modules.Catalog.Features.Events.SetEventPrice;
using PulseReserve.Modules.Catalog.Features.Events.StartSale;
using PulseReserve.UnitTests.Catalog.Fakes;
using Xunit;

namespace PulseReserve.UnitTests.Catalog.Features.Events;

public class EventFeatureTests
{
    private readonly FakeVenueRepository _venueRepository = new();
    private readonly FakeSeatingLayoutRepository _layoutRepository = new();
    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeCatalogUnitOfWork _unitOfWork = new();
    private readonly TestDateTimeProvider _dateTimeProvider = new();

    private async Task<(VenueId VenueId, SeatingLayoutId LayoutId, PriceTierId TierId)> BuildValidLayoutAsync()
    {
        var venueId = VenueId.New();
        var venue = Venue.Create(venueId, "Symphony Center", Address.Create("Street", "City", "Country", "123").Value).Value;
        await _venueRepository.AddAsync(venue);

        var layoutId = SeatingLayoutId.New();
        var layout = SeatingLayout.Create(layoutId, venueId, "Orchestra Hall").Value;

        var section = Section.Create(SectionId.New(), "Floor", 1).Value;
        layout.AddSection(section);

        var row = Row.Create(RowId.New(), section.Id, "Row A", 1).Value;
        layout.AddRow(row);

        var tierId = PriceTierId.New();
        var tier = PriceTier.Create(tierId, "Tier A").Value;
        layout.AddPriceTier(tier);

        var seat = Seat.Create(SeatId.New(), row.Id, section.Id, "Seat 1", SeatCoordinate.Create(1, 1).Value, tierId).Value;
        layout.AddSeat(seat);

        await _layoutRepository.AddAsync(layout);
        return (venueId, layoutId, tierId);
    }

    [Fact]
    public async Task CreateEvent_Succeeds()
    {
        var (venueId, layoutId, _) = await BuildValidLayoutAsync();
        var handler = new CreateEventHandler(_venueRepository, _layoutRepository, _eventRepository, _unitOfWork);
        var eventId = EventId.New();
        var startUtc = _dateTimeProvider.UtcNow.AddDays(10);
        var endUtc = startUtc.AddHours(3);

        var command = new CreateEventCommand(
            eventId,
            "Beethoven Symphony No. 9",
            "Masterpiece Concert",
            venueId,
            startUtc,
            endUtc,
            layoutId);

        var result = await handler.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(eventId);
        _unitOfWork.SaveCount.Should().Be(1);

        var persisted = await _eventRepository.GetByIdAsync(eventId);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(EventStatus.Draft);
        persisted.SeatingLayoutId.Should().Be(layoutId);
    }

    [Fact]
    public async Task SetEventPrice_Succeeds()
    {
        var (venueId, layoutId, tierId) = await BuildValidLayoutAsync();
        var eventId = EventId.New();
        var evt = Event.CreateDraft(eventId, "Rock Concert", "Live", venueId, _dateTimeProvider.UtcNow.AddDays(5), _dateTimeProvider.UtcNow.AddDays(5).AddHours(2)).Value;
        evt.AssignSeatingLayout(layoutId);
        await _eventRepository.AddAsync(evt);

        var handler = new SetEventPriceHandler(_eventRepository, _unitOfWork);
        var result = await handler.HandleAsync(new SetEventPriceCommand(eventId, tierId, 75.50m, "USD"));

        result.IsSuccess.Should().BeTrue();
        var priceResult = evt.GetPrice(tierId);
        priceResult.IsSuccess.Should().BeTrue();
        priceResult.Value.Amount.Should().Be(75.50m);
        priceResult.Value.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task PublishEvent_WhenPrerequisitesAreValid_Succeeds()
    {
        var (venueId, layoutId, tierId) = await BuildValidLayoutAsync();
        var eventId = EventId.New();
        var evt = Event.CreateDraft(eventId, "Ballet Gala", "Dance", venueId, _dateTimeProvider.UtcNow.AddDays(7), _dateTimeProvider.UtcNow.AddDays(7).AddHours(3)).Value;
        evt.AssignSeatingLayout(layoutId);
        evt.SetPrice(tierId, Money.Create(100m, "USD").Value);
        await _eventRepository.AddAsync(evt);

        var handler = new PublishEventHandler(_eventRepository, _layoutRepository, _unitOfWork, _dateTimeProvider);
        var result = await handler.HandleAsync(new PublishEventCommand(eventId));

        result.IsSuccess.Should().BeTrue();
        evt.Status.Should().Be(EventStatus.Published);
        evt.PublishedAtUtc.Should().Be(_dateTimeProvider.UtcNow);
    }

    [Fact]
    public async Task PublishEvent_WhenLayoutIsInvalid_Fails()
    {
        var venueId = VenueId.New();
        var venue = Venue.Create(venueId, "Venue", Address.Create("S", "C", "C", "1").Value).Value;
        await _venueRepository.AddAsync(venue);

        // Empty layout with no sections, rows, seats, or tiers
        var emptyLayoutId = SeatingLayoutId.New();
        var emptyLayout = SeatingLayout.Create(emptyLayoutId, venueId, "Empty Layout").Value;
        await _layoutRepository.AddAsync(emptyLayout);

        var eventId = EventId.New();
        var evt = Event.CreateDraft(eventId, "Gala", "Desc", venueId, _dateTimeProvider.UtcNow.AddDays(1), _dateTimeProvider.UtcNow.AddDays(1).AddHours(2)).Value;
        evt.AssignSeatingLayout(emptyLayoutId);
        await _eventRepository.AddAsync(evt);

        var handler = new PublishEventHandler(_eventRepository, _layoutRepository, _unitOfWork, _dateTimeProvider);
        var result = await handler.HandleAsync(new PublishEventCommand(eventId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatingLayout.NoSections");
        evt.Status.Should().Be(EventStatus.Draft);
    }

    [Fact]
    public async Task PublishEvent_WhenPricingIsMissing_Fails()
    {
        var (venueId, layoutId, _) = await BuildValidLayoutAsync();
        var eventId = EventId.New();
        var evt = Event.CreateDraft(eventId, "Unpriced Concert", "Live", venueId, _dateTimeProvider.UtcNow.AddDays(5), _dateTimeProvider.UtcNow.AddDays(5).AddHours(2)).Value;
        evt.AssignSeatingLayout(layoutId);
        // Do not configure price for tierId
        await _eventRepository.AddAsync(evt);

        var handler = new PublishEventHandler(_eventRepository, _layoutRepository, _unitOfWork, _dateTimeProvider);
        var result = await handler.HandleAsync(new PublishEventCommand(eventId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Event.IncompletePricing");
        evt.Status.Should().Be(EventStatus.Draft);
    }

    [Fact]
    public async Task StartSale_WhenPrerequisitesAreValid_Succeeds()
    {
        var (venueId, layoutId, tierId) = await BuildValidLayoutAsync();
        var eventId = EventId.New();
        var evt = Event.CreateDraft(eventId, "Opera Evening", "Classic", venueId, _dateTimeProvider.UtcNow.AddDays(14), _dateTimeProvider.UtcNow.AddDays(14).AddHours(3)).Value;
        evt.AssignSeatingLayout(layoutId);
        evt.SetPrice(tierId, Money.Create(120m, "USD").Value);
        evt.Publish(_dateTimeProvider.UtcNow.AddDays(-1));
        await _eventRepository.AddAsync(evt);

        var handler = new StartSaleHandler(_eventRepository, _layoutRepository, _unitOfWork, _dateTimeProvider);
        var result = await handler.HandleAsync(new StartSaleCommand(eventId));

        result.IsSuccess.Should().BeTrue();
        evt.Status.Should().Be(EventStatus.OnSale);
        evt.OnSaleAtUtc.Should().Be(_dateTimeProvider.UtcNow);

        var layout = await _layoutRepository.GetByIdAsync(layoutId);
        layout!.IsLocked.Should().BeTrue();
    }

    [Fact]
    public async Task StartSale_WhenEventIsNotPublished_Fails()
    {
        var (venueId, layoutId, tierId) = await BuildValidLayoutAsync();
        var eventId = EventId.New();
        var evt = Event.CreateDraft(eventId, "Draft Event", "Classic", venueId, _dateTimeProvider.UtcNow.AddDays(14), _dateTimeProvider.UtcNow.AddDays(14).AddHours(3)).Value;
        evt.AssignSeatingLayout(layoutId);
        evt.SetPrice(tierId, Money.Create(120m, "USD").Value);
        // Event is still in Draft status!
        await _eventRepository.AddAsync(evt);

        var handler = new StartSaleHandler(_eventRepository, _layoutRepository, _unitOfWork, _dateTimeProvider);
        var result = await handler.HandleAsync(new StartSaleCommand(eventId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Event.NotPublished");
        evt.Status.Should().Be(EventStatus.Draft);

        var layout = await _layoutRepository.GetByIdAsync(layoutId);
        layout!.IsLocked.Should().BeFalse();
    }

    [Fact]
    public async Task PublishEvent_WhenEventIsOnSale_FailsAndDoesNotPersist()
    {
        var (venueId, layoutId, tierId) = await BuildValidLayoutAsync();
        var eventId = EventId.New();
        var evt = Event.CreateDraft(eventId, "Sold Out Show", "Classic", venueId, _dateTimeProvider.UtcNow.AddDays(14), _dateTimeProvider.UtcNow.AddDays(14).AddHours(3)).Value;
        evt.AssignSeatingLayout(layoutId);
        evt.SetPrice(tierId, Money.Create(120m, "USD").Value);
        evt.Publish(_dateTimeProvider.UtcNow.AddDays(-2));
        evt.StartSale(_dateTimeProvider.UtcNow.AddDays(-1));
        await _eventRepository.AddAsync(evt);

        var handler = new PublishEventHandler(_eventRepository, _layoutRepository, _unitOfWork, _dateTimeProvider);
        var result = await handler.HandleAsync(new PublishEventCommand(eventId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Event.AlreadyPublished");
        evt.Status.Should().Be(EventStatus.OnSale);
        _unitOfWork.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task GetEventById_WhenExists_ReturnsResponse()
    {
        var (venueId, layoutId, tierId) = await BuildValidLayoutAsync();
        var eventId = EventId.New();
        var evt = Event.CreateDraft(eventId, "Jazz Night", "Cool Jazz", venueId, _dateTimeProvider.UtcNow.AddDays(3), _dateTimeProvider.UtcNow.AddDays(3).AddHours(2)).Value;
        evt.AssignSeatingLayout(layoutId);
        evt.SetPrice(tierId, Money.Create(50m, "USD").Value);
        await _eventRepository.AddAsync(evt);

        var handler = new GetEventByIdHandler(_eventRepository);
        var result = await handler.HandleAsync(new GetEventByIdQuery(eventId));

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(eventId);
        result.Value.Name.Should().Be("Jazz Night");
        result.Value.Prices.Should().ContainSingle(p => p.PriceTierId == tierId && p.Amount == 50m);
    }

    [Fact]
    public async Task GetEventById_WhenNotFound_ReturnsFailure()
    {
        var handler = new GetEventByIdHandler(_eventRepository);
        var result = await handler.HandleAsync(new GetEventByIdQuery(EventId.New()));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Event.NotFound");
    }
}
