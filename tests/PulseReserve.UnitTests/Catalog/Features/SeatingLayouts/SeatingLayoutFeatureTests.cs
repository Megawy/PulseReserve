using FluentAssertions;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.Modules.Catalog.Domain.Venues;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddPriceTier;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddRow;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSeat;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSection;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.CreateSeatingLayout;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.GetSeatingLayoutById;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.LockSeatingLayout;
using PulseReserve.UnitTests.Catalog.Fakes;
using Xunit;

namespace PulseReserve.UnitTests.Catalog.Features.SeatingLayouts;

public class SeatingLayoutFeatureTests
{
    private readonly FakeVenueRepository _venueRepository = new();
    private readonly FakeSeatingLayoutRepository _layoutRepository = new();
    private readonly FakeCatalogUnitOfWork _unitOfWork = new();

    private async Task<VenueId> SeedVenueAsync()
    {
        var venueId = VenueId.New();
        var venue = Venue.Create(venueId, "Center Arena", Address.Create("Street", "City", "Country", "12345").Value).Value;
        await _venueRepository.AddAsync(venue);
        return venueId;
    }

    [Fact]
    public async Task CreateSeatingLayout_Succeeds()
    {
        var venueId = await SeedVenueAsync();
        var handler = new CreateSeatingLayoutHandler(_venueRepository, _layoutRepository, _unitOfWork);
        var layoutId = SeatingLayoutId.New();

        var result = await handler.HandleAsync(new CreateSeatingLayoutCommand(layoutId, venueId, "Standard Concert Layout"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(layoutId);
        _unitOfWork.SaveCount.Should().Be(1);

        var persisted = await _layoutRepository.GetByIdAsync(layoutId);
        persisted.Should().NotBeNull();
        persisted!.Name.Should().Be("Standard Concert Layout");
        persisted.VenueId.Should().Be(venueId);
    }

    [Fact]
    public async Task CreateSeatingLayout_WhenVenueDoesNotExist_Fails()
    {
        var handler = new CreateSeatingLayoutHandler(_venueRepository, _layoutRepository, _unitOfWork);
        var result = await handler.HandleAsync(new CreateSeatingLayoutCommand(SeatingLayoutId.New(), VenueId.New(), "Orphan Layout"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Venue.NotFound");
    }

    [Fact]
    public async Task AddSection_Succeeds()
    {
        var venueId = await SeedVenueAsync();
        var createHandler = new CreateSeatingLayoutHandler(_venueRepository, _layoutRepository, _unitOfWork);
        var layoutId = SeatingLayoutId.New();
        await createHandler.HandleAsync(new CreateSeatingLayoutCommand(layoutId, venueId, "Layout 1"));

        var addSectionHandler = new AddSectionHandler(_layoutRepository, _unitOfWork);
        var sectionId = SectionId.New();
        var result = await addSectionHandler.HandleAsync(new AddSectionCommand(layoutId, sectionId, "Main Floor", 1));

        result.IsSuccess.Should().BeTrue();
        var layout = await _layoutRepository.GetByIdAsync(layoutId);
        layout!.Sections.Should().ContainSingle(s => s.Id == sectionId && s.Name == "Main Floor");
    }

    [Fact]
    public async Task AddRow_Succeeds()
    {
        var venueId = await SeedVenueAsync();
        var layoutId = SeatingLayoutId.New();
        var sectionId = SectionId.New();

        var createHandler = new CreateSeatingLayoutHandler(_venueRepository, _layoutRepository, _unitOfWork);
        await createHandler.HandleAsync(new CreateSeatingLayoutCommand(layoutId, venueId, "Layout 1"));
        var addSectionHandler = new AddSectionHandler(_layoutRepository, _unitOfWork);
        await addSectionHandler.HandleAsync(new AddSectionCommand(layoutId, sectionId, "Section A", 1));

        var addRowHandler = new AddRowHandler(_layoutRepository, _unitOfWork);
        var rowId = RowId.New();
        var result = await addRowHandler.HandleAsync(new AddRowCommand(layoutId, rowId, sectionId, "Row A", 1));

        result.IsSuccess.Should().BeTrue();
        var layout = await _layoutRepository.GetByIdAsync(layoutId);
        layout!.Rows.Should().ContainSingle(r => r.Id == rowId && r.Label == "Row A");
    }

    [Fact]
    public async Task AddPriceTier_Succeeds()
    {
        var venueId = await SeedVenueAsync();
        var layoutId = SeatingLayoutId.New();
        var createHandler = new CreateSeatingLayoutHandler(_venueRepository, _layoutRepository, _unitOfWork);
        await createHandler.HandleAsync(new CreateSeatingLayoutCommand(layoutId, venueId, "Layout 1"));

        var addTierHandler = new AddPriceTierHandler(_layoutRepository, _unitOfWork);
        var tierId = PriceTierId.New();
        var result = await addTierHandler.HandleAsync(new AddPriceTierCommand(layoutId, tierId, "VIP Tier"));

        result.IsSuccess.Should().BeTrue();
        var layout = await _layoutRepository.GetByIdAsync(layoutId);
        layout!.PriceTiers.Should().ContainSingle(t => t.Id == tierId && t.Name == "VIP Tier");
    }

    [Fact]
    public async Task AddSeat_Succeeds()
    {
        var venueId = await SeedVenueAsync();
        var layoutId = SeatingLayoutId.New();
        var sectionId = SectionId.New();
        var rowId = RowId.New();
        var tierId = PriceTierId.New();

        var createHandler = new CreateSeatingLayoutHandler(_venueRepository, _layoutRepository, _unitOfWork);
        await createHandler.HandleAsync(new CreateSeatingLayoutCommand(layoutId, venueId, "Layout 1"));

        var addSectionHandler = new AddSectionHandler(_layoutRepository, _unitOfWork);
        await addSectionHandler.HandleAsync(new AddSectionCommand(layoutId, sectionId, "Section A", 1));

        var addRowHandler = new AddRowHandler(_layoutRepository, _unitOfWork);
        await addRowHandler.HandleAsync(new AddRowCommand(layoutId, rowId, sectionId, "Row 1", 1));

        var addTierHandler = new AddPriceTierHandler(_layoutRepository, _unitOfWork);
        await addTierHandler.HandleAsync(new AddPriceTierCommand(layoutId, tierId, "Standard"));

        var addSeatHandler = new AddSeatHandler(_layoutRepository, _unitOfWork);
        var seatId = SeatId.New();
        var result = await addSeatHandler.HandleAsync(new AddSeatCommand(
            layoutId,
            seatId,
            rowId,
            sectionId,
            "Seat 1",
            1,
            1,
            tierId));

        result.IsSuccess.Should().BeTrue();
        var layout = await _layoutRepository.GetByIdAsync(layoutId);
        layout!.Seats.Should().ContainSingle(s => s.Id == seatId && s.Label == "Seat 1");
    }

    [Fact]
    public async Task LockSeatingLayout_ForStructurallyValidLayout_Succeeds()
    {
        var venueId = await SeedVenueAsync();
        var layoutId = SeatingLayoutId.New();
        var sectionId = SectionId.New();
        var rowId = RowId.New();
        var tierId = PriceTierId.New();
        var seatId = SeatId.New();

        var createHandler = new CreateSeatingLayoutHandler(_venueRepository, _layoutRepository, _unitOfWork);
        await createHandler.HandleAsync(new CreateSeatingLayoutCommand(layoutId, venueId, "Layout Complete"));

        await new AddSectionHandler(_layoutRepository, _unitOfWork).HandleAsync(new AddSectionCommand(layoutId, sectionId, "Sec 1", 1));
        await new AddRowHandler(_layoutRepository, _unitOfWork).HandleAsync(new AddRowCommand(layoutId, rowId, sectionId, "Row 1", 1));
        await new AddPriceTierHandler(_layoutRepository, _unitOfWork).HandleAsync(new AddPriceTierCommand(layoutId, tierId, "Tier 1"));
        await new AddSeatHandler(_layoutRepository, _unitOfWork).HandleAsync(new AddSeatCommand(layoutId, seatId, rowId, sectionId, "S1", 1, 1, tierId));

        var lockHandler = new LockSeatingLayoutHandler(_layoutRepository, _unitOfWork);
        var result = await lockHandler.HandleAsync(new LockSeatingLayoutCommand(layoutId));

        result.IsSuccess.Should().BeTrue();
        var layout = await _layoutRepository.GetByIdAsync(layoutId);
        layout!.IsLocked.Should().BeTrue();
    }

    [Fact]
    public async Task LockSeatingLayout_ForIncompleteLayout_Fails()
    {
        var venueId = await SeedVenueAsync();
        var layoutId = SeatingLayoutId.New();
        var createHandler = new CreateSeatingLayoutHandler(_venueRepository, _layoutRepository, _unitOfWork);
        await createHandler.HandleAsync(new CreateSeatingLayoutCommand(layoutId, venueId, "Incomplete Layout"));

        // Only add section, no rows, seats, or tiers
        await new AddSectionHandler(_layoutRepository, _unitOfWork).HandleAsync(new AddSectionCommand(layoutId, SectionId.New(), "Sec 1", 1));

        var lockHandler = new LockSeatingLayoutHandler(_layoutRepository, _unitOfWork);
        var result = await lockHandler.HandleAsync(new LockSeatingLayoutCommand(layoutId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatingLayout.NoRows");

        var layout = await _layoutRepository.GetByIdAsync(layoutId);
        layout!.IsLocked.Should().BeFalse();
    }

    [Fact]
    public async Task GetSeatingLayoutById_WhenExists_ReturnsResponse()
    {
        var venueId = await SeedVenueAsync();
        var layoutId = SeatingLayoutId.New();
        var createHandler = new CreateSeatingLayoutHandler(_venueRepository, _layoutRepository, _unitOfWork);
        await createHandler.HandleAsync(new CreateSeatingLayoutCommand(layoutId, venueId, "Queryable Layout"));

        var queryHandler = new GetSeatingLayoutByIdHandler(_layoutRepository);
        var result = await queryHandler.HandleAsync(new GetSeatingLayoutByIdQuery(layoutId));

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(layoutId);
        result.Value.Name.Should().Be("Queryable Layout");
    }

    [Fact]
    public async Task GetSeatingLayoutById_WhenNotFound_ReturnsFailure()
    {
        var queryHandler = new GetSeatingLayoutByIdHandler(_layoutRepository);
        var result = await queryHandler.HandleAsync(new GetSeatingLayoutByIdQuery(SeatingLayoutId.New()));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatingLayout.NotFound");
    }
}
