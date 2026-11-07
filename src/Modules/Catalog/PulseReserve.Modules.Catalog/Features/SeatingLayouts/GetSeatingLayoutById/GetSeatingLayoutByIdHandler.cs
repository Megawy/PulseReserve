using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.GetSeatingLayoutById;

public sealed class GetSeatingLayoutByIdHandler
{
    private readonly ISeatingLayoutRepository _seatingLayoutRepository;

    public GetSeatingLayoutByIdHandler(ISeatingLayoutRepository seatingLayoutRepository)
    {
        _seatingLayoutRepository = seatingLayoutRepository;
    }

    public async Task<Result<SeatingLayoutResponse>> HandleAsync(
        GetSeatingLayoutByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        var layout = await _seatingLayoutRepository.GetByIdAsync(
            query.LayoutId,
            cancellationToken: cancellationToken);

        if (layout is null)
            return Result.Failure<SeatingLayoutResponse>(Error.NotFound("SeatingLayout.NotFound", $"Seating layout with ID '{query.LayoutId}' was not found."));

        return Result.Success(new SeatingLayoutResponse(
            layout.Id,
            layout.VenueId,
            layout.Name,
            layout.IsLocked,
            layout.Sections.Select(s => new SectionResponse(s.Id, s.Name, s.DisplayOrder)).ToList(),
            layout.Rows.Select(r => new RowResponse(r.Id, r.SectionId, r.Label, r.DisplayOrder)).ToList(),
            layout.Seats.Select(s => new SeatResponse(s.Id, s.RowId, s.SectionId, s.Label, s.Coordinate.X, s.Coordinate.Y, s.PriceTierId)).ToList(),
            layout.PriceTiers.Select(t => new PriceTierResponse(t.Id, t.Name)).ToList()));
    }
}
