using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.GetSeatingLayoutById;

public sealed record SeatingLayoutResponse(
    SeatingLayoutId Id,
    VenueId VenueId,
    string Name,
    bool IsLocked,
    IReadOnlyCollection<SectionResponse> Sections,
    IReadOnlyCollection<RowResponse> Rows,
    IReadOnlyCollection<SeatResponse> Seats,
    IReadOnlyCollection<PriceTierResponse> PriceTiers);

public sealed record SectionResponse(
    SectionId Id,
    string Name,
    int DisplayOrder);

public sealed record RowResponse(
    RowId Id,
    SectionId SectionId,
    string Label,
    int DisplayOrder);

public sealed record SeatResponse(
    SeatId Id,
    RowId RowId,
    SectionId SectionId,
    string Label,
    int CoordinateX,
    int CoordinateY,
    PriceTierId PriceTierId);

public sealed record PriceTierResponse(
    PriceTierId Id,
    string Name);
