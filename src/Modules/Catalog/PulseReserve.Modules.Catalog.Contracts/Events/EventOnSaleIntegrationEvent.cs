namespace PulseReserve.Modules.Catalog.Contracts;

public sealed record EventOnSaleIntegrationEvent(
    EventId EventId,
    DateTime OnSaleAtUtc);
