using FluentAssertions;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using Xunit;

namespace PulseReserve.UnitTests.Catalog.Domain.Events;

public class EventDomainTests
{
    private readonly EventId _eventId = EventId.New();
    private readonly VenueId _venueId = VenueId.New();
    private readonly SeatingLayoutId _layoutId = SeatingLayoutId.New();
    private readonly DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CreateDraft_WithValidArguments_ShouldSucceedInDraftState()
    {
        // Arrange & Act
        var result = Event.CreateDraft(
            _eventId,
            "Grand Symphonic Concert",
            "A world-class orchestra performance",
            _venueId,
            _now.AddDays(10),
            _now.AddDays(10).AddHours(3));

        // Assert
        result.IsSuccess.Should().BeTrue();
        var evt = result.Value;
        evt.Id.Should().Be(_eventId);
        evt.Name.Should().Be("Grand Symphonic Concert");
        evt.Status.Should().Be(EventStatus.Draft);
        evt.SeatingLayoutId.Should().BeNull();
        evt.DomainEvents.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void CreateDraft_WithInvalidName_ShouldFail(string? invalidName)
    {
        // Act
        var result = Event.CreateDraft(
            _eventId,
            invalidName!,
            "Description",
            _venueId,
            _now.AddDays(1),
            _now.AddDays(2));

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Event.EmptyName");
    }

    [Fact]
    public void CreateDraft_WithStartTimeAfterEndTime_ShouldFail()
    {
        // Act
        var result = Event.CreateDraft(
            _eventId,
            "Invalid Event",
            "Description",
            _venueId,
            _now.AddDays(5),
            _now.AddDays(4)); // End before start

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Event.InvalidTimeRange");
    }

    [Fact]
    public void Event_WithoutSeatingLayoutId_CannotBePublished()
    {
        // Arrange: draft event has no linked layout
        var evt = CreateValidDraft();

        // Act
        var result = evt.Publish(_now);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Event.NoSeatingLayout");
        evt.Status.Should().Be(EventStatus.Draft);
    }

    [Fact]
    public void Event_WithSeatingLayoutId_CanBePublished_WhenEventRulesAreValid()
    {
        // Arrange
        var evt = CreateValidDraft();
        evt.AssignSeatingLayout(_layoutId);

        // Act: Event.Publish evaluates only Event-owned rules (linked layout ID, valid status)
        var result = evt.Publish(_now);

        // Assert
        result.IsSuccess.Should().BeTrue();
        evt.Status.Should().Be(EventStatus.Published);
        evt.PublishedAtUtc.Should().Be(_now);

        evt.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<EventPublishedDomainEvent>()
            .Which.Should().BeEquivalentTo(new EventPublishedDomainEvent(
                _eventId,
                _venueId,
                _layoutId,
                evt.ScheduledStartUtc,
                _now));
    }

    [Fact]
    public void StartSale_WhenPublished_ShouldSucceedAndRaiseOnSaleDomainEvent()
    {
        // Arrange
        var evt = CreateValidDraft();
        evt.AssignSeatingLayout(_layoutId);
        evt.Publish(_now);
        evt.ClearDomainEvents();

        // Act
        var saleStartTime = _now.AddHours(2);
        var result = evt.StartSale(saleStartTime);

        // Assert
        result.IsSuccess.Should().BeTrue();
        evt.Status.Should().Be(EventStatus.OnSale);
        evt.OnSaleAtUtc.Should().Be(saleStartTime);

        evt.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<EventOnSaleDomainEvent>()
            .Which.EventId.Should().Be(_eventId);
    }

    [Fact]
    public void AssignSeatingLayout_WhenOnSale_ShouldFailDueToStructuralImmutability()
    {
        // Arrange
        var evt = CreateValidDraft();
        evt.AssignSeatingLayout(_layoutId);
        evt.Publish(_now);
        evt.StartSale(_now.AddHours(1));

        // Act
        var newLayoutId = SeatingLayoutId.New();
        var result = evt.AssignSeatingLayout(newLayoutId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Event.CannotModifyOnSaleLayout");
        evt.SeatingLayoutId.Should().Be(_layoutId); // Preserved original layout
    }

    [Fact]
    public void Cancel_WhenActive_ShouldTransitionToCancelledAndPreventSubsequentPublish()
    {
        // Arrange
        var evt = CreateValidDraft();
        evt.AssignSeatingLayout(_layoutId);
        evt.Publish(_now);

        // Act
        var cancelResult = evt.Cancel("Artist illness", _now.AddDays(1));

        // Assert
        cancelResult.IsSuccess.Should().BeTrue();
        evt.Status.Should().Be(EventStatus.Cancelled);
        evt.CancellationReason.Should().Be("Artist illness");

        // Subsequent publish must fail
        var republishResult = evt.Publish(_now.AddDays(2));
        republishResult.IsFailure.Should().BeTrue();
        republishResult.Error.Code.Should().Be("Event.TerminalStatus");
    }

    [Fact]
    public void SetPrice_WithValidArguments_ShouldAddPriceToEvent()
    {
        // Arrange
        var evt = CreateValidDraft();
        var tierId = PriceTierId.New();
        var price = Money.Create(120.00m, "USD").Value;

        // Act
        var result = evt.SetPrice(tierId, price);

        // Assert
        result.IsSuccess.Should().BeTrue();
        evt.Prices.Should().ContainSingle();
        var retrievedPrice = evt.GetPrice(tierId);
        retrievedPrice.IsSuccess.Should().BeTrue();
        retrievedPrice.Value.Should().Be(price);
    }

    [Fact]
    public void SetPrice_WhenPriceAlreadyConfigured_ShouldUpdateExistingPrice()
    {
        // Arrange
        var evt = CreateValidDraft();
        var tierId = PriceTierId.New();
        var initialPrice = Money.Create(100.00m, "USD").Value;
        var updatedPrice = Money.Create(135.00m, "USD").Value;

        evt.SetPrice(tierId, initialPrice);

        // Act
        var result = evt.SetPrice(tierId, updatedPrice);

        // Assert
        result.IsSuccess.Should().BeTrue();
        evt.Prices.Should().ContainSingle();
        evt.GetPrice(tierId).Value.Should().Be(updatedPrice);
    }

    [Fact]
    public void SetPrice_WhenEventIsOnSale_ShouldFailDueToPricingImmutability()
    {
        // Arrange
        var evt = CreateValidDraft();
        evt.AssignSeatingLayout(_layoutId);
        evt.Publish(_now);
        evt.StartSale(_now.AddHours(1));

        var tierId = PriceTierId.New();
        var price = Money.Create(150.00m, "USD").Value;

        // Act
        var result = evt.SetPrice(tierId, price);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Event.CannotModifyOnSalePricing");
    }

    [Fact]
    public void DifferentEvents_CanUseSameSeatingLayout_WithDifferentPrices_ForSamePriceTier()
    {
        // Arrange: Structural reusable layout with structural PriceTier
        var layout = SeatingLayout.Create(_layoutId, _venueId, "Main Hall").Value;
        var vipTier = PriceTier.Create(PriceTierId.New(), "VIP").Value;
        var regularTier = PriceTier.Create(PriceTierId.New(), "Regular").Value;
        layout.AddPriceTier(vipTier);
        layout.AddPriceTier(regularTier);

        var section = Section.Create(SectionId.New(), "Main Section", 1).Value;
        layout.AddSection(section);
        var row = Row.Create(RowId.New(), section.Id, "Row 1", 1).Value;
        layout.AddRow(row);
        var seat1 = Seat.Create(SeatId.New(), row.Id, section.Id, "1", SeatCoordinate.Create(1, 1).Value, vipTier.Id).Value;
        var seat2 = Seat.Create(SeatId.New(), row.Id, section.Id, "2", SeatCoordinate.Create(2, 1).Value, regularTier.Id).Value;
        layout.AddSeat(seat1);
        layout.AddSeat(seat2);
        layout.LockLayout().IsSuccess.Should().BeTrue();

        // Event A: Premium Weekend Concert
        var eventA = Event.CreateDraft(
            EventId.New(),
            "Saturday Night Live",
            "Weekend headliner",
            _venueId,
            _now.AddDays(7),
            _now.AddDays(7).AddHours(3)).Value;
        eventA.AssignSeatingLayout(layout.Id);
        eventA.SetPrice(vipTier.Id, Money.Create(100.00m, "USD").Value);
        eventA.SetPrice(regularTier.Id, Money.Create(60.00m, "USD").Value);

        // Event B: Midweek Acoustic Matinee
        var eventB = Event.CreateDraft(
            EventId.New(),
            "Tuesday Acoustic Matinee",
            "Midweek acoustic session",
            _venueId,
            _now.AddDays(10),
            _now.AddDays(10).AddHours(3)).Value;
        eventB.AssignSeatingLayout(layout.Id);
        eventB.SetPrice(vipTier.Id, Money.Create(70.00m, "USD").Value);
        eventB.SetPrice(regularTier.Id, Money.Create(40.00m, "USD").Value);

        // Assert: Both events share the same layout reference
        eventA.SeatingLayoutId.Should().Be(layout.Id);
        eventB.SeatingLayoutId.Should().Be(layout.Id);

        // Assert: Event A and Event B have distinct, event-specific prices for identical structural tiers
        eventA.GetPrice(vipTier.Id).Value.Amount.Should().Be(100.00m);
        eventA.GetPrice(regularTier.Id).Value.Amount.Should().Be(60.00m);

        eventB.GetPrice(vipTier.Id).Value.Amount.Should().Be(70.00m);
        eventB.GetPrice(regularTier.Id).Value.Amount.Should().Be(40.00m);

        // Assert: SeatingLayout remains pristine and does not mutate or hold prices
        layout.PriceTiers.Should().HaveCount(2);
        layout.PriceTiers.Select(pt => pt.Name).Should().Contain(["VIP", "Regular"]);
    }

    private Event CreateValidDraft() =>
        Event.CreateDraft(
            _eventId,
            "Summer Music Festival",
            "A rock concert in the arena",
            _venueId,
            _now.AddDays(30),
            _now.AddDays(30).AddHours(5)).Value;
}
