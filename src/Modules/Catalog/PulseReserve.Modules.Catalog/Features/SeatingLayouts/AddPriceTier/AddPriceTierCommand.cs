using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddPriceTier;

public sealed record AddPriceTierCommand(
    SeatingLayoutId LayoutId,
    PriceTierId PriceTierId,
    string Name,
    int? ExpectedVersion = null);
