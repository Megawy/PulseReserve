using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.Events;

public sealed record EventPublishedDomainEvent(
    EventId EventId,
    VenueId VenueId,
    SeatingLayoutId SeatingLayoutId,
    DateTime ScheduledStartUtc,
    DateTime OccurredOnUtc) : IDomainEvent;

public sealed record EventOnSaleDomainEvent(
    EventId EventId,
    DateTime OccurredOnUtc) : IDomainEvent;

public sealed record EventCancelledDomainEvent(
    EventId EventId,
    string Reason,
    DateTime OccurredOnUtc) : IDomainEvent;
