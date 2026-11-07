namespace PulseReserve.AppHost.Live;

/// <summary>Client method names. They are part of the wire contract with the Angular client.</summary>
public static class LiveTargets
{
    public const string Availability = "availability";
    public const string Reservation = "reservation";
    public const string Order = "order";
    public const string Admin = "admin";
    public const string Resync = "resync";
}

/// <summary>SignalR group names. A connection joins <c>user:</c> and <c>admins</c> from its authenticated identity only.</summary>
public static class LiveGroups
{
    public const string Admins = "admins";

    public static string Event(Guid eventId) => $"event:{eventId:D}";

    public static string User(Guid userId) => $"user:{userId:D}";
}

/// <summary>
/// Identifies this process and orders its messages. A client ignores a message whose sequence is not greater than the last one it
/// applied for the same epoch, and treats a new epoch (another instance, or a restart) as "my baseline is gone: re-read everything".
/// </summary>
public sealed class LiveEpoch
{
    private long _sequence;

    public Guid Value { get; } = Guid.NewGuid();

    public long Next() => Interlocked.Increment(ref _sequence);
}

public sealed record SeatDelta(Guid SeatId, string Status);

/// <summary>Public availability: which seats of an event are now Available, Held or Sold. Carries no information about WHO holds a seat.</summary>
public sealed record AvailabilityMessage(Guid EventId, IReadOnlyList<SeatDelta> Seats, Guid Epoch, long Seq);

/// <summary>Sent only to the reservation's owner. The server's status and expiry; the client's countdown is presentation only.</summary>
public sealed record ReservationMessage(Guid ReservationId, Guid EventId, string Status, DateTime ExpiresAtUtc, Guid Epoch, long Seq);

/// <summary>
/// Sent only to the order's owner. A HINT to re-read the order: the authoritative state is whatever the authenticated order API
/// returns. It never states that a payment succeeded, a refund was made or a ticket exists beyond the counts the owner can already see.
/// </summary>
public sealed record OrderMessage(Guid OrderId, Guid ReservationId, string Status, string? PaymentStatus, int TicketCount, Guid Epoch, long Seq);

/// <summary>Sent only to administrators: "this area changed, re-read it". No financial detail.</summary>
public sealed record AdminMessage(string Area, Guid Epoch, long Seq);

/// <summary>Sent to every client: notifications may have been missed, so re-read what you display.</summary>
public sealed record ResyncMessage(string Reason, Guid Epoch, long Seq);

/// <summary>The answer to the hub's <c>Hello</c>: who the server believes this connection is, and its epoch.</summary>
public sealed record LiveHello(Guid Epoch, DateTime ServerTimeUtc, bool Authenticated, bool Admin, int MaxConnectionSeconds);
