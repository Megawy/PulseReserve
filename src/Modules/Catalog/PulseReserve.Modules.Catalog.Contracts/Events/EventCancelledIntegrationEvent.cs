namespace PulseReserve.Modules.Catalog.Contracts;

public sealed record EventCancelledIntegrationEvent(
    EventId EventId,
    string Reason,
    DateTime CancelledAtUtc);
