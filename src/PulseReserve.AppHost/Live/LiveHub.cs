using Microsoft.AspNetCore.SignalR;
using PulseReserve.AppHost.Security;

namespace PulseReserve.AppHost.Live;

/// <summary>
/// The one real-time endpoint (<c>/hubs/live</c>). It only ever PUSHES: the client sends nothing but subscription requests, and a
/// subscription grants nothing that is not already public.
/// <list type="bullet">
/// <item>The identity comes from the session cookie that authenticated the connection request (the principal is rebuilt from the
/// database, so roles are current at connect time). A signed-in connection joins <c>user:{id}</c>; an administrator also joins
/// <c>admins</c>. Nobody can join another user's group: the hub exposes no method that takes a user id.</item>
/// <item><c>SubscribeEvent</c> joins <c>event:{id}</c>, which receives seat availability only (which seats are Available, Held or Sold;
/// never who holds them). Anonymous visitors may subscribe, as the availability endpoint is public. At most
/// <see cref="LiveOptions.MaxEventSubscriptions"/> events per connection.</item>
/// <item>Connections are closed after <see cref="LiveOptions.MaxConnectionAge"/> (see <see cref="LiveConnectionReaper"/>).</item>
/// </list>
/// Messages are hints to re-read; the authoritative state is always the authenticated REST API.
/// </summary>
public sealed class LiveHub(LiveConnectionRegistry registry, LiveOptions options, LiveEpoch epoch, TimeProvider clock) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var principal = Context.User;
        var userId = principal?.Identity?.IsAuthenticated == true ? principal.GetUserId() : null;
        var admin = userId is not null && principal!.IsInRole(AuthPolicies.AdminRole);

        // Register first, then join groups: a connection that is aborted halfway never lingers in the registry.
        registry.Add(Context, userId, admin, clock.GetUtcNow().UtcDateTime);
        if (userId is { } id)
            await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.User(id));
        if (admin)
            await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.Admins);

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Who the server believes this connection is, and this process's epoch (the client's baseline for sequence checks).</summary>
    public LiveHello Hello()
    {
        var principal = Context.User;
        var authenticated = principal?.Identity?.IsAuthenticated == true && principal.GetUserId() is not null;
        return new LiveHello(
            epoch.Value, clock.GetUtcNow().UtcDateTime, authenticated, authenticated && principal!.IsInRole(AuthPolicies.AdminRole),
            (int)options.MaxConnectionAge.TotalSeconds);
    }

    public async Task SubscribeEvent(Guid eventId)
    {
        if (eventId == Guid.Empty)
            throw new HubException("Live.InvalidEvent");

        if (!registry.TryAddSubscription(Context.ConnectionId, eventId, options.MaxEventSubscriptions))
            throw new HubException("Live.TooManySubscriptions");

        await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.Event(eventId));
    }

    public async Task UnsubscribeEvent(Guid eventId)
    {
        if (eventId == Guid.Empty)
            return;

        registry.RemoveSubscription(Context.ConnectionId, eventId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, LiveGroups.Event(eventId));
    }
}
