using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.Events.PublishEvent;

public sealed record PublishEventCommand(
    EventId EventId,
    int? ExpectedVersion = null);
