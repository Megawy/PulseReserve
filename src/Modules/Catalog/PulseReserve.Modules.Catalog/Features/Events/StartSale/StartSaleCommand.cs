using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.Events.StartSale;

public sealed record StartSaleCommand(
    EventId EventId,
    int? ExpectedEventVersion = null,
    int? ExpectedLayoutVersion = null);
