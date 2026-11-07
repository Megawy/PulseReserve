using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.Events.SetEventPrice;

public sealed record SetEventPriceCommand(
    EventId EventId,
    PriceTierId PriceTierId,
    decimal PriceAmount,
    string Currency,
    int? ExpectedVersion = null);
