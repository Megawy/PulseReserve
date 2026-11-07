using FluentAssertions;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Features.Venues.CreateVenue;
using PulseReserve.Modules.Catalog.Features.Venues.GetVenueById;
using PulseReserve.UnitTests.Catalog.Fakes;
using Xunit;

namespace PulseReserve.UnitTests.Catalog.Features.Venues;

public class VenueFeatureTests
{
    private readonly FakeVenueRepository _venueRepository = new();
    private readonly FakeCatalogUnitOfWork _unitOfWork = new();

    [Fact]
    public async Task CreateVenue_WithValidData_SucceedsAndPersists()
    {
        var handler = new CreateVenueHandler(_venueRepository, _unitOfWork);
        var venueId = VenueId.New();
        var command = new CreateVenueCommand(
            venueId,
            "Madison Square Garden",
            "4 Pennsylvania Plaza",
            "New York",
            "USA",
            "10001");

        var result = await handler.HandleAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(venueId);
        _unitOfWork.SaveCount.Should().Be(1);

        var persisted = await _venueRepository.GetByIdAsync(venueId);
        persisted.Should().NotBeNull();
        persisted!.Name.Should().Be("Madison Square Garden");
        persisted.Address.Street.Should().Be("4 Pennsylvania Plaza");
    }

    [Fact]
    public async Task CreateVenue_WithInvalidData_ReturnsFailure()
    {
        var handler = new CreateVenueHandler(_venueRepository, _unitOfWork);
        var command = new CreateVenueCommand(
            VenueId.New(),
            "", // Invalid name
            "Street",
            "City",
            "Country",
            "12345");

        var result = await handler.HandleAsync(command);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Venue.EmptyName");
        _unitOfWork.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task GetVenueById_WhenExists_ReturnsVenueResponse()
    {
        var createHandler = new CreateVenueHandler(_venueRepository, _unitOfWork);
        var venueId = VenueId.New();
        await createHandler.HandleAsync(new CreateVenueCommand(venueId, "Wembley Stadium", "Wembley Park", "London", "UK", "HA9 0WS"));

        var queryHandler = new GetVenueByIdHandler(_venueRepository);
        var result = await queryHandler.HandleAsync(new GetVenueByIdQuery(venueId));

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(venueId);
        result.Value.Name.Should().Be("Wembley Stadium");
        result.Value.City.Should().Be("London");
    }

    [Fact]
    public async Task GetVenueById_WhenNotFound_ReturnsFailure()
    {
        var queryHandler = new GetVenueByIdHandler(_venueRepository);
        var result = await queryHandler.HandleAsync(new GetVenueByIdQuery(VenueId.New()));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Venue.NotFound");
    }
}
