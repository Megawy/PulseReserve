using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Features.Events.PublishEvent;
using PulseReserve.Modules.Catalog.Features.Events.SetEventPrice;
using PulseReserve.Modules.Catalog.Features.Events.StartSale;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSection;
using PulseReserve.UnitTests.Catalog.Fakes;
using Xunit;

namespace PulseReserve.UnitTests.Catalog.Features.Concurrency;

public class ApplicationConcurrencyTests
{
    private readonly FakeSeatingLayoutRepository _layoutRepository = new();
    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeCatalogUnitOfWork _unitOfWork = new();
    private readonly TestDateTimeProvider _dateTimeProvider = new();

    [Fact]
    public async Task AddSectionCommand_PassesExpectedVersionToRepository()
    {
        var layoutId = SeatingLayoutId.New();
        var layout = SeatingLayout.Create(layoutId, VenueId.New(), "Layout").Value;
        await _layoutRepository.AddAsync(layout);

        var handler = new AddSectionHandler(_layoutRepository, _unitOfWork);
        await handler.HandleAsync(new AddSectionCommand(layoutId, SectionId.New(), "Sec 1", 1, ExpectedVersion: 5));

        _layoutRepository.LastRequestedExpectedVersion.Should().Be(5);
    }

    [Fact]
    public async Task AddSectionCommand_DoesNotSwallowConcurrencyException()
    {
        var layoutId = SeatingLayoutId.New();
        var layout = SeatingLayout.Create(layoutId, VenueId.New(), "Layout").Value;
        await _layoutRepository.AddAsync(layout);

        _unitOfWork.ExceptionToThrow = new DbUpdateConcurrencyException("Stale aggregate version detected in persistence.");

        var handler = new AddSectionHandler(_layoutRepository, _unitOfWork);
        var act = async () => await handler.HandleAsync(new AddSectionCommand(layoutId, SectionId.New(), "Sec 1", 1, ExpectedVersion: 1));

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>()
            .WithMessage("*Stale aggregate version detected in persistence.*");
    }

    [Fact]
    public async Task SetEventPriceCommand_PassesExpectedVersionToRepository()
    {
        var eventId = EventId.New();
        var evt = Event.CreateDraft(eventId, "Concert", "Live", VenueId.New(), DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(2)).Value;
        await _eventRepository.AddAsync(evt);

        var handler = new SetEventPriceHandler(_eventRepository, _unitOfWork);
        await handler.HandleAsync(new SetEventPriceCommand(eventId, PriceTierId.New(), 50m, "USD", ExpectedVersion: 3));

        _eventRepository.LastRequestedExpectedVersion.Should().Be(3);
    }

    [Fact]
    public async Task PublishEventCommand_PassesExpectedVersionToRepository()
    {
        var eventId = EventId.New();
        var evt = Event.CreateDraft(eventId, "Concert", "Live", VenueId.New(), DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(2)).Value;
        await _eventRepository.AddAsync(evt);

        var handler = new PublishEventHandler(_eventRepository, _layoutRepository, _unitOfWork, _dateTimeProvider);
        await handler.HandleAsync(new PublishEventCommand(eventId, ExpectedVersion: 7));

        _eventRepository.LastRequestedExpectedVersion.Should().Be(7);
    }

    [Fact]
    public async Task StartSaleCommand_PassesExpectedVersionsToRepositories()
    {
        var layoutId = SeatingLayoutId.New();
        var layout = SeatingLayout.Create(layoutId, VenueId.New(), "Layout").Value;
        await _layoutRepository.AddAsync(layout);

        var eventId = EventId.New();
        var evt = Event.CreateDraft(eventId, "Concert", "Live", VenueId.New(), DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(2)).Value;
        evt.AssignSeatingLayout(layoutId);
        evt.Publish(DateTime.UtcNow);
        await _eventRepository.AddAsync(evt);

        var handler = new StartSaleHandler(_eventRepository, _layoutRepository, _unitOfWork, _dateTimeProvider);
        await handler.HandleAsync(new StartSaleCommand(eventId, ExpectedEventVersion: 4, ExpectedLayoutVersion: 2));

        _eventRepository.LastRequestedExpectedVersion.Should().Be(4);
        _layoutRepository.LastRequestedExpectedVersion.Should().Be(2);
    }
}
