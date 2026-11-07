using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PulseReserve.AppHost.Live;
using PulseReserve.Modules.Payments.Features.Live;
using PulseReserve.Modules.Reservation.Features.Live;
using Xunit;

namespace PulseReserve.UnitTests.Live;

/// <summary>
/// Authorization and routing of the REAL hub and dispatcher, over real SignalR client connections. The only fake is the database
/// (<see cref="FakeLiveStateReader"/>): these tests prove who may hear what, not SQL. Negative assertions ("Bob heard nothing") use a
/// marker message sent AFTER the messages under test: messages reach one connection in order, so once the marker has arrived
/// everything before it has too, and an empty list is a real result rather than a timeout.
/// </summary>
public sealed class LiveHubTests
{
    private static readonly Guid Alice = Guid.Parse("a11ce000-0000-4000-8000-000000000001");
    private static readonly Guid Bob = Guid.Parse("b0b00000-0000-4000-8000-000000000002");
    private static readonly Guid Carol = Guid.Parse("ca401000-0000-4000-8000-000000000003");
    private static readonly Guid Ev1 = Guid.Parse("e0000000-0000-4000-8000-000000000001");
    private static readonly Guid Ev2 = Guid.Parse("e0000000-0000-4000-8000-000000000002");
    private static readonly Guid Seat1 = Guid.Parse("5ea70000-0000-4000-8000-000000000001");
    private static readonly Guid Seat2 = Guid.Parse("5ea70000-0000-4000-8000-000000000002");
    private static readonly Guid Seat3 = Guid.Parse("5ea70000-0000-4000-8000-000000000003");
    private static readonly Guid Res1 = Guid.Parse("12e50000-0000-4000-8000-000000000001");
    private static readonly Guid Res2 = Guid.Parse("12e50000-0000-4000-8000-000000000002");
    private static readonly Guid Ord1 = Guid.Parse("0dd00000-0000-4000-8000-000000000001");
    private static readonly Guid Ord2 = Guid.Parse("0dd00000-0000-4000-8000-000000000002");

    private static ReservationLiveSnapshot Reservation(Guid id, Guid eventId, Guid customer, string status = "Held", params (Guid Seat, string Status)[] seats) =>
        new(id, eventId, customer, status, new DateTime(2030, 1, 1, 10, 15, 0, DateTimeKind.Utc), seats.Select(s => new SeatLiveState(s.Seat, s.Status)).ToList());

    private static OrderLiveSnapshot Order(Guid id, Guid customer, Guid reservation, string status = "PendingPayment", string? payment = "RequiresAction", int tickets = 0) =>
        new(id, customer, reservation, status, payment, tickets);

    private static Task DispatchAsync(LiveTestHost host, params LiveSignal[] signals) =>
        host.Dispatcher.DispatchAsync(signals, CancellationToken.None);

    /// <summary>Sends a resync marker and waits for it on every client: everything dispatched before it has been delivered by then.</summary>
    private static async Task SettleAsync(LiveTestHost host, params LiveClient[] clients)
    {
        var reason = "marker-" + Guid.NewGuid().ToString("N");
        await DispatchAsync(host, LiveSignal.ResyncAll(reason));
        foreach (var client in clients)
            await client.WaitForAsync(LiveTargets.Resync, body => body.GetProperty("reason").GetString() == reason);
    }

    private static Guid[] SeatIds(JsonElement body) =>
        body.GetProperty("seats").EnumerateArray().Select(s => s.GetProperty("seatId").GetGuid()).ToArray();

    // ---- identity ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Hello_TellsEachConnectionWhoTheServerBelievesItIs()
    {
        await using var host = await LiveTestHost.StartAsync();
        await using var anonymous = await host.Connect().StartAsync();
        await using var customer = await host.Connect(Alice).StartAsync();
        await using var admin = await host.Connect(Carol, admin: true).StartAsync();
        await using var forgedAdminHeader = await host.Connect(admin: true).StartAsync(); // the admin marker without a signed-in user

        anonymous.Hello.Should().BeEquivalentTo(new { Authenticated = false, Admin = false });
        customer.Hello.Should().BeEquivalentTo(new { Authenticated = true, Admin = false });
        admin.Hello.Should().BeEquivalentTo(new { Authenticated = true, Admin = true });
        forgedAdminHeader.Hello.Should().BeEquivalentTo(new { Authenticated = false, Admin = false });

        foreach (var client in new[] { anonymous, customer, admin })
        {
            client.Hello.Epoch.Should().Be(host.Epoch.Value);
            client.Hello.MaxConnectionSeconds.Should().Be(600);
            client.Hello.ServerTimeUtc.Should().Be(host.Clock.Now.UtcDateTime);
        }
    }

    [Fact]
    public async Task TheHubOffersNoWayToJoinAnotherUsersOrTheAdminGroup()
    {
        // The only methods a client can call, and none takes a group or a user.
        var methods = typeof(LiveHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetBaseDefinition().DeclaringType == typeof(LiveHub) && !m.IsSpecialName)
            .ToList();

        methods.Select(m => m.Name).Should().BeEquivalentTo("Hello", "SubscribeEvent", "UnsubscribeEvent");
        methods.SelectMany(m => m.GetParameters()).Should().OnlyContain(p => p.ParameterType == typeof(Guid) && p.Name == "eventId");

        await using var host = await LiveTestHost.StartAsync();
        await using var attacker = await host.Connect(Bob).StartAsync();
        foreach (var name in new[] { "AddToGroup", "JoinGroup", "Subscribe", "SubscribeUser", "JoinAdmins" })
        {
            var act = () => attacker.Connection.InvokeAsync(name, "admins");
            await act.Should().ThrowAsync<HubException>();
        }
    }

    // ---- routing and authorization ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAnonymousVisitor_HearsSeatAvailabilityOnlyForTheEventItSubscribedTo_AndNeverWhoHoldsAnything()
    {
        await using var host = await LiveTestHost.StartAsync();
        host.Reader.Reservations[Res1] = Reservation(Res1, Ev1, Alice, "Held", (Seat1, "Held"), (Seat2, "Held"));
        host.Reader.Reservations[Res2] = Reservation(Res2, Ev2, Bob, "Held", (Seat3, "Held"));
        await using var visitor = await host.Connect().StartAsync();
        await visitor.SubscribeAsync(Ev1);

        await DispatchAsync(host, LiveSignal.ForReservation(Res1), LiveSignal.ForReservation(Res2));
        await SettleAsync(host, visitor);

        var availability = visitor.Messages(LiveTargets.Availability);
        availability.Should().ContainSingle();
        availability[0].Body.GetProperty("eventId").GetGuid().Should().Be(Ev1);
        SeatIds(availability[0].Body).Should().BeEquivalentTo([Seat1, Seat2]);
        availability[0].Body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("eventId", "seats", "epoch", "seq");

        var wire = availability[0].Body.GetRawText();
        wire.Should().NotContain(Alice.ToString()).And.NotContain(Bob.ToString()).And.NotContain(Res1.ToString());

        visitor.Messages(LiveTargets.Reservation).Should().BeEmpty();
        visitor.Messages(LiveTargets.Order).Should().BeEmpty();
        visitor.Messages(LiveTargets.Admin).Should().BeEmpty();
    }

    [Fact]
    public async Task AReservationAndAnOrder_GoOnlyToTheirOwner_EvenToAnAdministratorOrAnotherSubscriberOfTheEvent()
    {
        await using var host = await LiveTestHost.StartAsync();
        host.Reader.Reservations[Res1] = Reservation(Res1, Ev1, Alice, "Checkout", (Seat1, "Held"));
        host.Reader.Orders[Ord1] = Order(Ord1, Alice, Res1);
        host.Reader.Reservations[Res2] = Reservation(Res2, Ev1, Bob, "Held", (Seat2, "Held"));
        host.Reader.Orders[Ord2] = Order(Ord2, Bob, Res2);

        await using var alice = await host.Connect(Alice).StartAsync();
        await using var aliceSecondTab = await host.Connect(Alice).StartAsync();
        await using var bob = await host.Connect(Bob).StartAsync();
        await using var admin = await host.Connect(Carol, admin: true).StartAsync();
        await using var visitor = await host.Connect().StartAsync();
        foreach (var client in new[] { alice, bob, admin, visitor })
            await client.SubscribeAsync(Ev1); // all of them watch the same event

        await DispatchAsync(host, LiveSignal.ForReservation(Res1), LiveSignal.ForOrder(Ord1));
        await SettleAsync(host, alice, aliceSecondTab, bob, admin, visitor);

        foreach (var tab in new[] { alice, aliceSecondTab })
        {
            tab.Messages(LiveTargets.Reservation).Should().ContainSingle().Which.Body.GetProperty("reservationId").GetGuid().Should().Be(Res1);
            tab.Messages(LiveTargets.Order).Should().ContainSingle().Which.Body.GetProperty("orderId").GetGuid().Should().Be(Ord1);
        }

        foreach (var other in new[] { bob, admin, visitor })
        {
            other.Messages(LiveTargets.Reservation).Should().BeEmpty();
            other.Messages(LiveTargets.Order).Should().BeEmpty();
        }

        // Public availability still reaches every subscriber of the event; it names no owner.
        foreach (var watcher in new[] { bob, admin, visitor })
            watcher.Messages(LiveTargets.Availability).Should().ContainSingle();

        await DispatchAsync(host, LiveSignal.ForReservation(Res2), LiveSignal.ForOrder(Ord2));
        await SettleAsync(host, alice, aliceSecondTab, bob);

        bob.Messages(LiveTargets.Reservation).Should().ContainSingle().Which.Body.GetProperty("reservationId").GetGuid().Should().Be(Res2);
        bob.Messages(LiveTargets.Order).Should().ContainSingle().Which.Body.GetProperty("orderId").GetGuid().Should().Be(Ord2);
        alice.Messages(LiveTargets.Reservation).Should().ContainSingle(); // nothing of Bob's reached Alice
        alice.Messages(LiveTargets.Order).Should().ContainSingle();
    }

    [Fact]
    public async Task AnAdministrator_ReceivesTheirOwnPrivateMessagesLikeAnyoneElse_AndNothingOfOthers()
    {
        await using var host = await LiveTestHost.StartAsync();
        host.Reader.Orders[Ord1] = Order(Ord1, Carol, Res1);
        host.Reader.Orders[Ord2] = Order(Ord2, Alice, Res2);
        await using var admin = await host.Connect(Carol, admin: true).StartAsync();

        await DispatchAsync(host, LiveSignal.ForOrder(Ord1), LiveSignal.ForOrder(Ord2));
        await SettleAsync(host, admin);

        admin.Messages(LiveTargets.Order).Should().ContainSingle().Which.Body.GetProperty("orderId").GetGuid().Should().Be(Ord1);
    }

    [Fact]
    public async Task AdministratorAreaMessages_GoToAdministratorsOnly_AndOnlyForKnownAreas()
    {
        await using var host = await LiveTestHost.StartAsync();
        await using var admin = await host.Connect(Carol, admin: true).StartAsync();
        await using var customer = await host.Connect(Alice).StartAsync();
        await using var visitor = await host.Connect().StartAsync();
        await using var forged = await host.Connect(admin: true).StartAsync();

        await DispatchAsync(host, LiveSignal.ForAdmin(LiveAreas.Orders), LiveSignal.ForAdmin(LiveAreas.Overview), LiveSignal.ForAdmin("not-an-area"), LiveSignal.ForAdmin(""));
        await SettleAsync(host, admin, customer, visitor, forged);

        admin.Messages(LiveTargets.Admin).Select(m => m.Body.GetProperty("area").GetString()).Should().BeEquivalentTo(LiveAreas.Orders, LiveAreas.Overview);
        foreach (var other in new[] { customer, visitor, forged })
            other.Messages(LiveTargets.Admin).Should().BeEmpty();

        // An admin message carries the area and ordering fields only: no financial detail.
        admin.Messages(LiveTargets.Admin)[0].Body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("area", "epoch", "seq");
    }

    [Fact]
    public async Task AnOrderMessage_IsAHintThatCarriesNoAmountsProvidersOrReferences()
    {
        await using var host = await LiveTestHost.StartAsync();
        host.Reader.Orders[Ord1] = Order(Ord1, Alice, Res1, "Paid", "Succeeded", 2);
        await using var alice = await host.Connect(Alice).StartAsync();

        await DispatchAsync(host, LiveSignal.ForOrder(Ord1));
        await SettleAsync(host, alice);

        var body = alice.Messages(LiveTargets.Order).Should().ContainSingle().Which.Body;
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("orderId", "reservationId", "status", "paymentStatus", "ticketCount", "epoch", "seq");
        body.GetProperty("status").GetString().Should().Be("Paid");
        body.GetProperty("paymentStatus").GetString().Should().Be("Succeeded");
        body.GetProperty("ticketCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task AReservationMessage_CarriesTheServerStatusAndExpiry_AsUtc()
    {
        await using var host = await LiveTestHost.StartAsync();
        host.Reader.Reservations[Res1] = Reservation(Res1, Ev1, Alice, "Expired");
        await using var alice = await host.Connect(Alice).StartAsync();

        await DispatchAsync(host, LiveSignal.ForReservation(Res1));
        await SettleAsync(host, alice);

        var body = alice.Messages(LiveTargets.Reservation).Should().ContainSingle().Which.Body;
        body.GetProperty("status").GetString().Should().Be("Expired");
        body.GetProperty("expiresAtUtc").GetDateTime().Should().Be(new DateTime(2030, 1, 1, 10, 15, 0, DateTimeKind.Utc));
        body.GetProperty("expiresAtUtc").GetString().Should().EndWith("Z");
    }

    [Fact]
    public async Task ResyncReachesEveryConnection_SignedInOrNot()
    {
        await using var host = await LiveTestHost.StartAsync();
        await using var visitor = await host.Connect().StartAsync();
        await using var customer = await host.Connect(Alice).StartAsync();
        await using var admin = await host.Connect(Carol, admin: true).StartAsync();

        await DispatchAsync(host, LiveSignal.ResyncAll("change-feed-connected"));

        foreach (var client in new[] { visitor, customer, admin })
        {
            var message = await client.WaitForAsync(LiveTargets.Resync, body => body.GetProperty("reason").GetString() == "change-feed-connected");
            message.Body.GetProperty("epoch").GetGuid().Should().Be(host.Epoch.Value);
        }
    }

    // ---- ordering, content comes from the database, duplicates -------------------------------------------------------------

    [Fact]
    public async Task EveryMessageCarriesTheProcessEpochAndAStrictlyIncreasingSequence()
    {
        await using var host = await LiveTestHost.StartAsync();
        host.Reader.Reservations[Res1] = Reservation(Res1, Ev1, Alice, "Held", (Seat1, "Held"));
        host.Reader.Orders[Ord1] = Order(Ord1, Alice, Res1);
        await using var alice = await host.Connect(Alice).StartAsync();
        await alice.SubscribeAsync(Ev1);

        for (var i = 0; i < 3; i++)
            await DispatchAsync(host, LiveSignal.ForReservation(Res1), LiveSignal.ForOrder(Ord1), LiveSignal.ForAdmin(LiveAreas.Orders));
        await SettleAsync(host, alice);

        var all = alice.Messages();
        all.Should().HaveCountGreaterThan(6);
        all.Select(m => m.Body.GetProperty("epoch").GetGuid()).Distinct().Should().ContainSingle().Which.Should().Be(host.Epoch.Value);
        var sequence = all.Select(m => m.Body.GetProperty("seq").GetInt64()).ToList();
        sequence.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void TwoProcessesNeverShareAnEpoch_AndSequenceNumbersAreUniqueUnderConcurrency()
    {
        new LiveEpoch().Value.Should().NotBe(new LiveEpoch().Value);

        var epoch = new LiveEpoch();
        var numbers = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, 2000, _ => numbers.Add(epoch.Next()));

        numbers.Distinct().Should().HaveCount(2000);
        numbers.Min().Should().Be(1);
        numbers.Max().Should().Be(2000);
    }

    [Fact]
    public async Task TheContentIsWhatTheDatabaseSaysNow_NotWhatTheSignalImplied()
    {
        await using var host = await LiveTestHost.StartAsync();
        // The signal could have been about the hold being created; by dispatch time the database says it has ended.
        host.Reader.Reservations[Res1] = Reservation(Res1, Ev1, Alice, "Expired", (Seat1, "Available"), (Seat2, "Available"));
        await using var alice = await host.Connect(Alice).StartAsync();
        await alice.SubscribeAsync(Ev1);

        await DispatchAsync(host, LiveSignal.ForReservation(Res1));
        await SettleAsync(host, alice);

        var availability = alice.Messages(LiveTargets.Availability).Should().ContainSingle().Which.Body;
        availability.GetProperty("seats").EnumerateArray().Select(s => s.GetProperty("status").GetString()).Should().OnlyContain(s => s == "Available");
        alice.Messages(LiveTargets.Reservation).Should().ContainSingle().Which.Body.GetProperty("status").GetString().Should().Be("Expired");

        // A late duplicate of an old signal reads the same current state again; it cannot resurrect the hold.
        host.Reader.Reservations[Res1] = Reservation(Res1, Ev1, Alice, "Confirmed", (Seat1, "Sold"), (Seat2, "Sold"));
        await DispatchAsync(host, LiveSignal.ForReservation(Res1));
        await SettleAsync(host, alice);

        alice.Messages(LiveTargets.Reservation).Last().Body.GetProperty("status").GetString().Should().Be("Confirmed");
    }

    [Fact]
    public async Task ABurstOfDuplicateSignals_CostsOneReadAndOneMessagePerEntity()
    {
        await using var host = await LiveTestHost.StartAsync();
        host.Reader.Reservations[Res1] = Reservation(Res1, Ev1, Alice, "Held", (Seat1, "Held"));
        host.Reader.Orders[Ord1] = Order(Ord1, Alice, Res1);
        await using var alice = await host.Connect(Alice).StartAsync();
        await alice.SubscribeAsync(Ev1);

        var burst = Enumerable.Repeat(LiveSignal.ForReservation(Res1), 5).Concat(Enumerable.Repeat(LiveSignal.ForOrder(Ord1), 4)).Concat(Enumerable.Repeat(LiveSignal.ForAdmin(LiveAreas.Orders), 3));
        await host.Dispatcher.DispatchAsync(burst.ToList(), CancellationToken.None);
        await SettleAsync(host, alice);

        alice.Messages(LiveTargets.Availability).Should().ContainSingle();
        alice.Messages(LiveTargets.Reservation).Should().ContainSingle();
        alice.Messages(LiveTargets.Order).Should().ContainSingle();
    }

    [Fact]
    public async Task ASignalForSomethingThatNoLongerExists_SendsNothingAndDoesNotFail()
    {
        await using var host = await LiveTestHost.StartAsync();
        await using var alice = await host.Connect(Alice).StartAsync();

        var act = () => DispatchAsync(host, LiveSignal.ForReservation(Res1), LiveSignal.ForOrder(Ord1), LiveSignal.ForReservation(Guid.Empty), LiveSignal.ForOrder(Guid.Empty));
        await act.Should().NotThrowAsync();
        await SettleAsync(host, alice);

        alice.Messages(LiveTargets.Reservation).Should().BeEmpty();
        alice.Messages(LiveTargets.Order).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnreachableDatabase_NeverStopsTheBatch_AndTheNextBatchRecovers()
    {
        await using var host = await LiveTestHost.StartAsync();
        host.Reader.Reservations[Res1] = Reservation(Res1, Ev1, Alice, "Held", (Seat1, "Held"));
        host.Reader.Orders[Ord1] = Order(Ord1, Alice, Res1);
        await using var alice = await host.Connect(Alice).StartAsync();
        await using var admin = await host.Connect(Carol, admin: true).StartAsync();

        host.Reader.Fail = true;
        var act = () => DispatchAsync(host, LiveSignal.ForReservation(Res1), LiveSignal.ForOrder(Ord1), LiveSignal.ForAdmin(LiveAreas.Orders));
        await act.Should().NotThrowAsync();
        await SettleAsync(host, alice, admin);

        alice.Messages(LiveTargets.Reservation).Should().BeEmpty();
        alice.Messages(LiveTargets.Order).Should().BeEmpty();
        admin.Messages(LiveTargets.Admin).Should().ContainSingle(); // the part that needs no read still went out

        host.Reader.Fail = false;
        await DispatchAsync(host, LiveSignal.ForReservation(Res1), LiveSignal.ForOrder(Ord1));
        await SettleAsync(host, alice);

        alice.Messages(LiveTargets.Reservation).Should().ContainSingle();
        alice.Messages(LiveTargets.Order).Should().ContainSingle();
    }

    [Fact]
    public async Task TheBackgroundLoop_DeliversASignalHandedToTheSink_WithoutBlockingTheCaller()
    {
        await using var host = await LiveTestHost.StartAsync();
        host.Reader.Orders[Ord1] = Order(Ord1, Alice, Res1, "Paid", "Succeeded", 1);
        await using var alice = await host.Connect(Alice).StartAsync();
        var before = host.Status.SignalsReceived;

        host.Sink.Enqueue(LiveSignal.ForOrder(Ord1)); // returns at once; the loop reads and sends after the coalescing delay

        await alice.WaitForAsync(LiveTargets.Order, body => body.GetProperty("orderId").GetGuid() == Ord1);
        host.Status.SignalsReceived.Should().Be(before + 1);
    }

    [Fact]
    public async Task WhenTheQueueOverflows_EveryClientIsToldToReRead_NotLeftWithASilentGap()
    {
        await using var host = await LiveTestHost.StartAsync();
        await using var visitor = await host.Connect().StartAsync();
        var tiny = new LiveDispatcher(
            host.App.Services.GetRequiredService<IServiceScopeFactory>(), host.App.Services.GetRequiredService<IHubContext<LiveHub>>(),
            host.Options with { ChannelCapacity = 100 }, host.Epoch, host.Status, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveDispatcher>.Instance);
        var droppedBefore = host.Status.SignalsDropped;
        var resyncsBefore = host.Status.Resyncs;

        for (var i = 0; i < 150; i++)
            tiny.Enqueue(LiveSignal.ForOrder(Guid.NewGuid()));
        await tiny.DispatchAsync([], CancellationToken.None);

        var message = await visitor.WaitForAsync(LiveTargets.Resync, body => body.GetProperty("reason").GetString() == "queue-overflow");
        message.Body.GetProperty("epoch").GetGuid().Should().Be(host.Epoch.Value);
        host.Status.SignalsDropped.Should().BeGreaterThan(droppedBefore);
        host.Status.Resyncs.Should().Be(resyncsBefore + 1);

        // The flag is consumed: an ordinary later pass does not announce an overflow again.
        await tiny.DispatchAsync([], CancellationToken.None);
        host.Status.Resyncs.Should().Be(resyncsBefore + 1);
    }

    // ---- subscriptions ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASubscription_IsCapped_IsIdempotent_AndCanBeReleased()
    {
        await using var host = await LiveTestHost.StartAsync();
        await using var client = await host.Connect().StartAsync();
        var events = Enumerable.Range(1, 6).Select(i => Guid.Parse($"e0000000-0000-4000-8000-0000000001{i:00}")).ToArray();

        foreach (var id in events.Take(5))
            await client.SubscribeAsync(id);
        await client.SubscribeAsync(events[0]); // already subscribed: not a sixth

        var tooMany = () => client.SubscribeAsync(events[5]);
        (await tooMany.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain("Live.TooManySubscriptions");
        host.Registry.SubscriptionCount(client.Connection.ConnectionId!).Should().Be(5);

        await client.Connection.InvokeAsync("UnsubscribeEvent", events[0]);
        await client.SubscribeAsync(events[5]);
        host.Registry.SubscriptionCount(client.Connection.ConnectionId!).Should().Be(5);

        var empty = () => client.SubscribeAsync(Guid.Empty);
        (await empty.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain("Live.InvalidEvent");
    }

    [Fact]
    public async Task TheSubscriptionLimit_FollowsTheConfiguration()
    {
        await using var host = await LiveTestHost.StartAsync(new() { ["Live:MaxEventSubscriptionsPerConnection"] = "1" });
        await using var client = await host.Connect().StartAsync();

        await client.SubscribeAsync(Ev1);
        var second = () => client.SubscribeAsync(Ev2);

        await second.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task AfterUnsubscribing_AvailabilityForThatEventStops()
    {
        await using var host = await LiveTestHost.StartAsync();
        host.Reader.Reservations[Res1] = Reservation(Res1, Ev1, Alice, "Held", (Seat1, "Held"));
        await using var visitor = await host.Connect().StartAsync();
        await visitor.SubscribeAsync(Ev1);
        await visitor.Connection.InvokeAsync("UnsubscribeEvent", Ev1);

        await DispatchAsync(host, LiveSignal.ForReservation(Res1));
        await SettleAsync(host, visitor);

        visitor.Messages(LiveTargets.Availability).Should().BeEmpty();
    }

    // ---- connection lifetime ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AConnectionOlderThanTheMaximumAge_IsClosed_SoItsIdentityIsCheckedAgainOnReconnect()
    {
        await using var host = await LiveTestHost.StartAsync();
        await using var client = await host.Connect(Alice).StartAsync();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        host.Registry.Count.Should().Be(1);
        var connectedAt = host.Clock.Now.UtcDateTime;

        host.Registry.AbortOlderThan(TimeSpan.FromMinutes(10), connectedAt.AddMinutes(9)).Should().Be(0);
        host.Registry.Count.Should().Be(1);

        host.Registry.AbortOlderThan(TimeSpan.FromMinutes(10), connectedAt.AddMinutes(10)).Should().Be(1);

        var winner = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        ReferenceEquals(winner, closed.Task).Should().BeTrue("the server aborts the connection and the client must notice");
        client.Connection.State.Should().Be(HubConnectionState.Disconnected);
        host.Registry.Count.Should().Be(0);
    }

    [Fact]
    public async Task ADisconnectedClient_LeavesTheRegistry()
    {
        await using var host = await LiveTestHost.StartAsync();
        var client = await host.Connect().StartAsync();
        host.Registry.Count.Should().Be(1);

        await client.Connection.StopAsync();
        await client.DisposeAsync();

        for (var i = 0; i < 200 && host.Registry.Count != 0; i++)
            await Task.Delay(10);
        host.Registry.Count.Should().Be(0);
    }

    // ---- HTTP surface: origin, rate limit, stateful reconnect, status, disabled -------------------------------------------

    private static async Task<HttpResponseMessage> NegotiateAsync(LiveTestHost host, string? origin, string query = "negotiateVersion=1")
    {
        using var client = host.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{LivePolicies.HubPath}/negotiate?{query}");
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task ABrowserPageOfAnotherSite_IsRefusedBeforeTheHubIsReached()
    {
        await using var host = await LiveTestHost.StartAsync();

        using var refused = await NegotiateAsync(host, "https://evil.example");
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("Live.OriginNotAllowed");

        using var opaque = await NegotiateAsync(host, "null");
        opaque.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var sameSite = await NegotiateAsync(host, "http://localhost");
        sameSite.StatusCode.Should().Be(HttpStatusCode.OK);

        using var noOrigin = await NegotiateAsync(host, null);
        noOrigin.StatusCode.Should().Be(HttpStatusCode.OK);

        var connect = async () => await host.Connect(Alice, origin: "https://evil.example").StartAsync();
        await connect.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task AnOriginOnTheAllowList_IsAccepted_AndOthersStillAreNot()
    {
        await using var host = await LiveTestHost.StartAsync(new() { ["Live:AllowedOrigins"] = "https://app.example.com" });

        using var listed = await NegotiateAsync(host, "https://app.example.com");
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var unlisted = await NegotiateAsync(host, "https://other.example.com");
        unlisted.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OpeningConnectionsIsRateLimitedPerClientAddress()
    {
        await using var host = await LiveTestHost.StartAsync(new() { ["Live:ConnectsPerMinute"] = "3" });

        for (var i = 0; i < 3; i++)
        {
            using var allowed = await NegotiateAsync(host, null);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var limited = await NegotiateAsync(host, null);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task StatefulReconnect_StaysDisabled_EvenWhenAClientAsksForIt()
    {
        // CVE-2026-70159 (GHSA-334f-vvp4-7g6p): the stateful-reconnect buffer can be driven to unbounded memory. The hub never
        // offers it, so the server never buffers for a client, whatever version of the framework it runs on.
        await using var host = await LiveTestHost.StartAsync();

        using var response = await NegotiateAsync(host, null, "negotiateVersion=1&UseStatefulReconnect=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var enabled = document.RootElement.EnumerateObject()
            .Where(p => p.Name.Equals("useStatefulReconnect", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Value.ValueKind == JsonValueKind.True)
            .SingleOrDefault();
        enabled.Should().BeFalse();
    }

    private static async Task<HttpResponseMessage> GetStatusAsync(LiveTestHost host, Guid? user, bool admin)
    {
        using var client = host.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/live/status");
        if (user is { } id)
            request.Headers.Add("X-Test-User", id.ToString());
        if (admin)
            request.Headers.Add("X-Test-Admin", "1");
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task TheStatusEndpoint_IsForAdministratorsOnly_AndRevealsNoSecret()
    {
        await using var host = await LiveTestHost.StartAsync();
        await using var connected = await host.Connect(Alice).StartAsync();

        using var anonymous = await GetStatusAsync(host, null, false);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var customer = await GetStatusAsync(host, Alice, false);
        customer.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await GetStatusAsync(host, Carol, true);
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await admin.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("connections").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("epoch").GetGuid().Should().Be(host.Epoch.Value);
        document.RootElement.GetProperty("maxConnectionSeconds").GetInt32().Should().Be(600);
        text.Should().NotContainEquivalentOf("password").And.NotContainEquivalentOf("Host=").And.NotContain(Alice.ToString());
    }

    [Fact]
    public async Task WhenDisabled_NoHubIsMapped_AndTheStatusSaysSo()
    {
        await using var host = await LiveTestHost.StartAsync(new() { ["Live:Enabled"] = "false" });

        using var negotiate = await NegotiateAsync(host, null);
        negotiate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.App.Services.GetService<LiveDispatcher>().Should().BeNull();

        using var status = await GetStatusAsync(host, Carol, true);
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }
}
