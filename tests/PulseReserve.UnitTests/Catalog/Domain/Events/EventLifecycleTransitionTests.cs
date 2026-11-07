using FluentAssertions;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.SharedKernel;
using Xunit;

namespace PulseReserve.UnitTests.Catalog.Domain.Events;

// Exhaustive lifecycle matrix: every (state, action) pair is either an allowed transition
// or a rejected one that leaves the event completely unchanged.
public class EventLifecycleTransitionTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    public enum LifecycleAction
    {
        Publish,
        StartSale,
        Close,
        Cancel
    }

    public static TheoryData<EventStatus, LifecycleAction, string> InvalidTransitions => new()
    {
        { EventStatus.Draft, LifecycleAction.StartSale, "Event.NotPublished" },
        { EventStatus.Draft, LifecycleAction.Close, "Event.NotOnSale" },

        { EventStatus.Published, LifecycleAction.Publish, "Event.AlreadyPublished" },
        { EventStatus.Published, LifecycleAction.Close, "Event.NotOnSale" },

        { EventStatus.OnSale, LifecycleAction.Publish, "Event.AlreadyPublished" },
        { EventStatus.OnSale, LifecycleAction.StartSale, "Event.NotPublished" },

        { EventStatus.Closed, LifecycleAction.Publish, "Event.TerminalStatus" },
        { EventStatus.Closed, LifecycleAction.StartSale, "Event.NotPublished" },
        { EventStatus.Closed, LifecycleAction.Close, "Event.NotOnSale" },
        { EventStatus.Closed, LifecycleAction.Cancel, "Event.AlreadyFinished" },

        { EventStatus.Cancelled, LifecycleAction.Publish, "Event.TerminalStatus" },
        { EventStatus.Cancelled, LifecycleAction.StartSale, "Event.NotPublished" },
        { EventStatus.Cancelled, LifecycleAction.Close, "Event.NotOnSale" },
        { EventStatus.Cancelled, LifecycleAction.Cancel, "Event.AlreadyFinished" }
    };

    public static TheoryData<EventStatus, LifecycleAction, EventStatus> ValidTransitions => new()
    {
        { EventStatus.Draft, LifecycleAction.Publish, EventStatus.Published },
        { EventStatus.Draft, LifecycleAction.Cancel, EventStatus.Cancelled },
        { EventStatus.Published, LifecycleAction.StartSale, EventStatus.OnSale },
        { EventStatus.Published, LifecycleAction.Cancel, EventStatus.Cancelled },
        { EventStatus.OnSale, LifecycleAction.Close, EventStatus.Closed },
        { EventStatus.OnSale, LifecycleAction.Cancel, EventStatus.Cancelled }
    };

    [Theory]
    [MemberData(nameof(InvalidTransitions))]
    public void InvalidTransition_IsRejected_AndLeavesEventUnchanged(
        EventStatus initialStatus,
        LifecycleAction action,
        string expectedErrorCode)
    {
        var evt = CreateEventIn(initialStatus);
        var before = Snapshot(evt);

        var result = Apply(evt, action, Now.AddDays(5));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(expectedErrorCode);
        Snapshot(evt).Should().Be(before);
        evt.DomainEvents.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(ValidTransitions))]
    public void ValidTransition_Succeeds(
        EventStatus initialStatus,
        LifecycleAction action,
        EventStatus expectedStatus)
    {
        var evt = CreateEventIn(initialStatus);

        var result = Apply(evt, action, Now.AddDays(5));

        result.IsSuccess.Should().BeTrue();
        evt.Status.Should().Be(expectedStatus);
    }

    [Fact]
    public void Matrix_CoversEveryStateAndActionPair()
    {
        var covered = InvalidTransitions.Select(row => ((EventStatus)row[0], (LifecycleAction)row[1]))
            .Concat(ValidTransitions.Select(row => ((EventStatus)row[0], (LifecycleAction)row[1])))
            .ToList();

        var allPairs = Enum.GetValues<EventStatus>()
            .SelectMany(status => Enum.GetValues<LifecycleAction>().Select(action => (status, action)));

        covered.Should().OnlyHaveUniqueItems();
        covered.Should().BeEquivalentTo(allPairs);
    }

    [Fact]
    public void OnSaleEvent_CannotBeRepublished_AndCannotRestartSale()
    {
        // Regression: Publish previously only rejected terminal states, so an OnSale event could
        // regress to Published and StartSale could then run a second time.
        var evt = CreateEventIn(EventStatus.OnSale);
        var publishedAt = evt.PublishedAtUtc;
        var onSaleAt = evt.OnSaleAtUtc;

        var republish = evt.Publish(Now.AddDays(7));
        var restartSale = evt.StartSale(Now.AddDays(8));

        republish.IsFailure.Should().BeTrue();
        republish.Error.Code.Should().Be("Event.AlreadyPublished");
        restartSale.IsFailure.Should().BeTrue();
        evt.Status.Should().Be(EventStatus.OnSale);
        evt.PublishedAtUtc.Should().Be(publishedAt);
        evt.OnSaleAtUtc.Should().Be(onSaleAt);
        evt.DomainEvents.Should().BeEmpty();
    }

    private static Result Apply(Event evt, LifecycleAction action, DateTime utcNow) => action switch
    {
        LifecycleAction.Publish => evt.Publish(utcNow),
        LifecycleAction.StartSale => evt.StartSale(utcNow),
        LifecycleAction.Close => evt.Close(utcNow),
        LifecycleAction.Cancel => evt.Cancel("Regression test", utcNow),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    // Drives a fresh event through the real domain methods into the requested state,
    // then clears staged domain events so assertions only observe the action under test.
    private static Event CreateEventIn(EventStatus status)
    {
        var evt = Event.CreateDraft(
            EventId.New(),
            "Lifecycle Event",
            "Transition matrix",
            VenueId.New(),
            Now.AddDays(30),
            Now.AddDays(30).AddHours(3)).Value;
        evt.AssignSeatingLayout(SeatingLayoutId.New()).IsSuccess.Should().BeTrue();

        if (status is EventStatus.Published or EventStatus.OnSale or EventStatus.Closed)
            evt.Publish(Now).IsSuccess.Should().BeTrue();

        if (status is EventStatus.OnSale or EventStatus.Closed)
            evt.StartSale(Now.AddHours(1)).IsSuccess.Should().BeTrue();

        if (status is EventStatus.Closed)
            evt.Close(Now.AddDays(31)).IsSuccess.Should().BeTrue();

        if (status is EventStatus.Cancelled)
            evt.Cancel("Setup", Now).IsSuccess.Should().BeTrue();

        evt.Status.Should().Be(status);
        evt.ClearDomainEvents();
        return evt;
    }

    private static (EventStatus, DateTime?, DateTime?, DateTime?, string?) Snapshot(Event evt) =>
        (evt.Status, evt.PublishedAtUtc, evt.OnSaleAtUtc, evt.CancelledAtUtc, evt.CancellationReason);
}
