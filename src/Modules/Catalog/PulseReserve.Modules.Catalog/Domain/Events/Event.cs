using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.Events;

// Event is the Aggregate Root for event performance schedule, commercial lifecycle, and event-scoped pricing.
// Concurrency Boundary: EventId + ExpectedVersion (independent from SeatingLayout concurrency).
// Cross-Aggregate Coupling: Holds only VenueId and SeatingLayoutId by reference; does not traverse external aggregate roots.
public sealed class Event : AggregateRoot<EventId>
{
    private readonly List<EventPrice> _prices = [];

    private Event(
        EventId id,
        string name,
        string description,
        VenueId venueId,
        DateTime scheduledStartUtc,
        DateTime scheduledEndUtc) : base(id)
    {
        Name = name;
        Description = description;
        VenueId = venueId;
        ScheduledStartUtc = scheduledStartUtc;
        ScheduledEndUtc = scheduledEndUtc;
        Status = EventStatus.Draft;
    }

    public string Name { get; private set; }
    public string Description { get; private set; }
    public VenueId VenueId { get; private set; }
    public SeatingLayoutId? SeatingLayoutId { get; private set; }
    public DateTime ScheduledStartUtc { get; private set; }
    public DateTime ScheduledEndUtc { get; private set; }
    public EventStatus Status { get; private set; }
    public DateTime? PublishedAtUtc { get; private set; }
    public DateTime? OnSaleAtUtc { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancellationReason { get; private set; }
    public IReadOnlyCollection<EventPrice> Prices => _prices.AsReadOnly();

    public static Result<Event> CreateDraft(
        EventId id,
        string name,
        string description,
        VenueId venueId,
        DateTime scheduledStartUtc,
        DateTime scheduledEndUtc)
    {
        if (id == EventId.Empty)
            return Result.Failure<Event>(new Error("Event.EmptyId", "Event identifier cannot be empty."));

        if (venueId == VenueId.Empty)
            return Result.Failure<Event>(new Error("Event.EmptyVenueId", "Venue identifier cannot be empty."));

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<Event>(new Error("Event.EmptyName", "Event name cannot be empty."));

        if (scheduledStartUtc >= scheduledEndUtc)
            return Result.Failure<Event>(new Error("Event.InvalidTimeRange", "Scheduled start time must be before end time."));

        return Result.Success(new Event(
            id,
            name.Trim(),
            description?.Trim() ?? string.Empty,
            venueId,
            scheduledStartUtc,
            scheduledEndUtc));
    }

    public Result AssignSeatingLayout(SeatingLayoutId layoutId)
    {
        if (layoutId == Contracts.SeatingLayoutId.Empty)
            return Result.Failure(new Error("Event.EmptyLayoutId", "Seating layout identifier cannot be empty."));

        if (Status == EventStatus.OnSale)
            return Result.Failure(new Error("Event.CannotModifyOnSaleLayout", "Cannot change seating layout once the event is OnSale."));

        if (Status is EventStatus.Closed or EventStatus.Cancelled)
            return Result.Failure(new Error("Event.TerminalStatus", $"Cannot assign seating layout to an event in '{Status}' state."));

        SeatingLayoutId = layoutId;
        return Result.Success();
    }

    public Result Publish(DateTime utcNow)
    {
        if (Status is EventStatus.Cancelled or EventStatus.Closed)
            return Result.Failure(new Error("Event.TerminalStatus", $"Cannot publish an event in '{Status}' state."));

        if (!SeatingLayoutId.HasValue || SeatingLayoutId.Value == Contracts.SeatingLayoutId.Empty)
            return Result.Failure(new Error("Event.NoSeatingLayout", "Event cannot be published without a linked seating layout."));

        Status = EventStatus.Published;
        PublishedAtUtc = utcNow;

        RaiseDomainEvent(new EventPublishedDomainEvent(
            Id,
            VenueId,
            SeatingLayoutId.Value,
            ScheduledStartUtc,
            utcNow));

        return Result.Success();
    }

    public Result StartSale(DateTime utcNow)
    {
        if (Status != EventStatus.Published)
            return Result.Failure(new Error("Event.NotPublished", "Event must be in Published state before transitioning to OnSale."));

        Status = EventStatus.OnSale;
        OnSaleAtUtc = utcNow;

        RaiseDomainEvent(new EventOnSaleDomainEvent(Id, utcNow));
        return Result.Success();
    }

    public Result Close(DateTime utcNow)
    {
        if (Status != EventStatus.OnSale)
            return Result.Failure(new Error("Event.NotOnSale", "Only active OnSale events can be closed."));

        Status = EventStatus.Closed;
        return Result.Success();
    }

    public Result Cancel(string reason, DateTime utcNow)
    {
        if (Status is EventStatus.Cancelled or EventStatus.Closed)
            return Result.Failure(new Error("Event.AlreadyFinished", $"Event is already in '{Status}' state and cannot be cancelled."));

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(new Error("Event.EmptyCancellationReason", "Cancellation reason cannot be empty."));

        Status = EventStatus.Cancelled;
        CancelledAtUtc = utcNow;
        CancellationReason = reason.Trim();

        RaiseDomainEvent(new EventCancelledDomainEvent(Id, CancellationReason, utcNow));
        return Result.Success();
    }

    // Pricing Immutability Domain Invariant:
    // Prices can be adjusted during Draft and Published states, but are strictly frozen once OnSale or in terminal states.
    public Result SetPrice(PriceTierId priceTierId, Money price)
    {
        if (Status is EventStatus.Closed or EventStatus.Cancelled)
            return Result.Failure(new Error("Event.TerminalStatus", $"Cannot modify pricing for an event in '{Status}' state."));

        if (Status == EventStatus.OnSale)
            return Result.Failure(new Error("Event.CannotModifyOnSalePricing", "Cannot modify pricing once the event is OnSale."));

        var priceResult = EventPrice.Create(priceTierId, price);
        if (priceResult.IsFailure)
            return priceResult;

        var existingIndex = _prices.FindIndex(p => p.PriceTierId == priceTierId);
        if (existingIndex >= 0)
        {
            _prices[existingIndex] = priceResult.Value;
        }
        else
        {
            _prices.Add(priceResult.Value);
        }

        return Result.Success();
    }

    public Result<Money> GetPrice(PriceTierId priceTierId)
    {
        var eventPrice = _prices.FirstOrDefault(p => p.PriceTierId == priceTierId);
        if (eventPrice is null)
            return Result.Failure<Money>(new Error("Event.PriceNotFound", $"Price for tier '{priceTierId}' is not configured for this event."));

        return Result.Success(eventPrice.Price);
    }
}
