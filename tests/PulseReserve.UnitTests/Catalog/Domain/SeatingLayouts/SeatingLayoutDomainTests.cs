using FluentAssertions;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using Xunit;

namespace PulseReserve.UnitTests.Catalog.Domain.SeatingLayouts;

public class SeatingLayoutDomainTests
{
    private readonly SeatingLayoutId _layoutId = SeatingLayoutId.New();
    private readonly VenueId _venueId = VenueId.New();

    [Fact]
    public void Money_WithNegativeAmount_ShouldFail()
    {
        // Act
        var result = Money.Create(-10.00m, "USD");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Money.NegativeAmount");
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDT")]
    [InlineData("")]
    [InlineData(null)]
    public void Money_WithInvalidCurrency_ShouldFail(string? invalidCurrency)
    {
        // Act
        var result = Money.Create(100.00m, invalidCurrency!);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Money.InvalidCurrency");
    }

    [Fact]
    public void SeatCoordinate_WithNegativeCoordinates_ShouldFail()
    {
        // Act
        var result = SeatCoordinate.Create(-1, 5);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatCoordinate.Negative");
    }

    [Fact]
    public void AddSection_WithDuplicateName_ShouldFail()
    {
        // Arrange
        var layout = SeatingLayout.Create(_layoutId, _venueId, "Main Arena").Value;
        var section1 = Section.Create(SectionId.New(), "Floor East", 1).Value;
        var section2 = Section.Create(SectionId.New(), "FLOOR EAST", 2).Value; // Duplicate name (case-insensitive)

        layout.AddSection(section1).IsSuccess.Should().BeTrue();

        // Act
        var result = layout.AddSection(section2);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatingLayout.DuplicateSectionName");
        layout.Sections.Should().ContainSingle();
    }

    [Fact]
    public void AddRow_WithDuplicateLabelInSameSection_ShouldFail()
    {
        // Arrange
        var layout = SeatingLayout.Create(_layoutId, _venueId, "Main Arena").Value;
        var section = Section.Create(SectionId.New(), "Balcony", 1).Value;
        layout.AddSection(section);

        var row1 = Row.Create(RowId.New(), section.Id, "A", 1).Value;
        var row2 = Row.Create(RowId.New(), section.Id, "a", 2).Value; // Duplicate label

        layout.AddRow(row1).IsSuccess.Should().BeTrue();

        // Act
        var result = layout.AddRow(row2);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatingLayout.DuplicateRowLabel");
        layout.Rows.Should().ContainSingle();
    }

    [Fact]
    public void AddSeat_WithDuplicateLabelInSameRow_ShouldFail()
    {
        // Arrange
        var layout = CreateLayoutWithSectionRowAndTier(out var section, out var row, out var tier);

        var coordinate1 = SeatCoordinate.Create(1, 1).Value;
        var coordinate2 = SeatCoordinate.Create(2, 1).Value;

        var seat1 = Seat.Create(SeatId.New(), row.Id, section.Id, "101", coordinate1, tier.Id).Value;
        var seat2 = Seat.Create(SeatId.New(), row.Id, section.Id, "101", coordinate2, tier.Id).Value; // Duplicate label

        layout.AddSeat(seat1).IsSuccess.Should().BeTrue();

        // Act
        var result = layout.AddSeat(seat2);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatingLayout.DuplicateSeatLabel");
        layout.Seats.Should().ContainSingle();
    }

    [Fact]
    public void AddSeat_ReferencingNonExistentPriceTier_ShouldFail()
    {
        // Arrange
        var layout = CreateLayoutWithSectionRowAndTier(out var section, out var row, out _);
        var missingTierId = PriceTierId.New();
        var coordinate = SeatCoordinate.Create(1, 1).Value;
        var seat = Seat.Create(SeatId.New(), row.Id, section.Id, "102", coordinate, missingTierId).Value;

        // Act
        var result = layout.AddSeat(seat);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatingLayout.PriceTierNotFound");
    }

    [Fact]
    public void ValidateStructure_OnIncompleteLayout_ShouldFail()
    {
        // Arrange: layout with no sections, rows, seats, or tiers
        var layout = SeatingLayout.Create(_layoutId, _venueId, "Empty Arena").Value;

        // Act
        var result = layout.ValidateStructure();

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void LockedLayout_CannotAddSection()
    {
        // Arrange
        var layout = CreateFullyPopulatedAndLockedLayout();
        var newSection = Section.Create(SectionId.New(), "New Balcony", 2).Value;

        // Act
        var result = layout.AddSection(newSection);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatingLayout.Locked");
    }

    [Fact]
    public void LockedLayout_CannotAddRow()
    {
        // Arrange
        var layout = CreateFullyPopulatedAndLockedLayout();
        var existingSection = layout.Sections.First();
        var newRow = Row.Create(RowId.New(), existingSection.Id, "Row B", 2).Value;

        // Act
        var result = layout.AddRow(newRow);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatingLayout.Locked");
    }

    [Fact]
    public void LockedLayout_CannotAddSeat()
    {
        // Arrange
        var layout = CreateFullyPopulatedAndLockedLayout();
        var existingSection = layout.Sections.First();
        var existingRow = layout.Rows.First();
        var existingTier = layout.PriceTiers.First();
        var newSeat = Seat.Create(SeatId.New(), existingRow.Id, existingSection.Id, "102", SeatCoordinate.Create(2, 1).Value, existingTier.Id).Value;

        // Act
        var result = layout.AddSeat(newSeat);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatingLayout.Locked");
    }

    [Fact]
    public void LockedLayout_CannotAddPriceTier()
    {
        // Arrange
        var layout = CreateFullyPopulatedAndLockedLayout();
        var newTier = PriceTier.Create(PriceTierId.New(), "Standard").Value;

        // Act
        var result = layout.AddPriceTier(newTier);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatingLayout.Locked");
    }

    [Fact]
    public void LockedLayout_RemainsReadable()
    {
        // Arrange
        var layout = CreateFullyPopulatedAndLockedLayout();

        // Assert
        layout.IsLocked.Should().BeTrue();
        layout.Sections.Should().NotBeEmpty();
        layout.Rows.Should().NotBeEmpty();
        layout.Seats.Should().NotBeEmpty();
        layout.PriceTiers.Should().NotBeEmpty();
        layout.Name.Should().Be("Standard Hall");
        layout.VenueId.Should().Be(_venueId);
    }

    [Fact]
    public void LockedLayout_CanBeReferencedConceptuallyByMultipleEvents()
    {
        // Arrange: Lock the layout
        var layout = CreateFullyPopulatedAndLockedLayout();
        var now = DateTime.UtcNow;

        // Act: Two separate events reference the same locked layout
        var event1 = Event.CreateDraft(
            EventId.New(),
            "Rock Night Day 1",
            "Day 1 performance",
            _venueId,
            now.AddDays(10),
            now.AddDays(10).AddHours(3)).Value;

        var event2 = Event.CreateDraft(
            EventId.New(),
            "Rock Night Day 2",
            "Day 2 performance",
            _venueId,
            now.AddDays(11),
            now.AddDays(11).AddHours(3)).Value;

        var assignResult1 = event1.AssignSeatingLayout(layout.Id);
        var assignResult2 = event2.AssignSeatingLayout(layout.Id);

        var publishResult1 = event1.Publish(now);
        var publishResult2 = event2.Publish(now);

        // Assert: Both events independently reference and publish with the exact same SeatingLayoutId
        assignResult1.IsSuccess.Should().BeTrue();
        assignResult2.IsSuccess.Should().BeTrue();
        publishResult1.IsSuccess.Should().BeTrue();
        publishResult2.IsSuccess.Should().BeTrue();

        event1.SeatingLayoutId.Should().Be(layout.Id);
        event2.SeatingLayoutId.Should().Be(layout.Id);
        event1.Status.Should().Be(EventStatus.Published);
        event2.Status.Should().Be(EventStatus.Published);
    }

    private SeatingLayout CreateFullyPopulatedAndLockedLayout()
    {
        var layout = CreateLayoutWithSectionRowAndTier(out var section, out var row, out var tier);
        var coordinate = SeatCoordinate.Create(1, 1).Value;
        var seat = Seat.Create(SeatId.New(), row.Id, section.Id, "101", coordinate, tier.Id).Value;
        layout.AddSeat(seat);
        layout.LockLayout().IsSuccess.Should().BeTrue();
        return layout;
    }

    private SeatingLayout CreateLayoutWithSectionRowAndTier(out Section section, out Row row, out PriceTier tier)
    {
        var layout = SeatingLayout.Create(_layoutId, _venueId, "Standard Hall").Value;

        tier = PriceTier.Create(PriceTierId.New(), "VIP Floor").Value;
        layout.AddPriceTier(tier);

        section = Section.Create(SectionId.New(), "Orchestra", 1).Value;
        layout.AddSection(section);

        row = Row.Create(RowId.New(), section.Id, "Row A", 1).Value;
        layout.AddRow(row);

        return layout;
    }
}
