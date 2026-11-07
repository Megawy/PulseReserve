using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.CreateSeatingLayout;

public sealed record CreateSeatingLayoutCommand(
    SeatingLayoutId Id,
    VenueId VenueId,
    string Name);
