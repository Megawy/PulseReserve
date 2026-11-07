using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSeat;

public sealed record AddSeatCommand(
    SeatingLayoutId LayoutId,
    SeatId SeatId,
    RowId RowId,
    SectionId SectionId,
    string Label,
    int CoordinateX,
    int CoordinateY,
    PriceTierId PriceTierId,
    int? ExpectedVersion = null);
