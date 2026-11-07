using System.Text.Json;

namespace PulseReserve.AppHost.Live;

public enum LiveSignalKind
{
    /// <summary>A reservation changed (hold, checkout, confirmation, release, expiry). <see cref="LiveSignal.Id"/> is its id.</summary>
    Reservation,

    /// <summary>An order, its payment, its tickets or its refund-driven status changed. <see cref="LiveSignal.Id"/> is the order id.</summary>
    Order,

    /// <summary>Something an administrator may want to re-read changed. <see cref="LiveSignal.Area"/> names it.</summary>
    Admin,

    /// <summary>Notifications may have been missed (listener reconnected, queue overflowed): every client must re-read what it shows.</summary>
    Resync
}

/// <summary>
/// An invalidation, never a fact: "something about this entity changed, go and read it". Content and routing are decided by reading
/// the current state from the database at dispatch time, so a late, duplicated or reordered signal can only cause a harmless read.
/// </summary>
public readonly record struct LiveSignal(LiveSignalKind Kind, Guid Id = default, string? Area = null, string? Reason = null)
{
    public static LiveSignal ForReservation(Guid id) => new(LiveSignalKind.Reservation, id);

    public static LiveSignal ForOrder(Guid id) => new(LiveSignalKind.Order, id);

    public static LiveSignal ForAdmin(string area) => new(LiveSignalKind.Admin, Area: area);

    public static LiveSignal ResyncAll(string reason) => new(LiveSignalKind.Resync, Reason: reason);
}

/// <summary>The administrator areas a signal can name (the console re-reads the matching list or the overview).</summary>
public static class LiveAreas
{
    public const string Overview = "overview";
    public const string Orders = "orders";
    public const string Refunds = "refunds";
    public const string Exceptions = "exceptions";
    public const string Anomalies = "anomalies";
    public const string Tasks = "tasks";
    public const string Outbox = "outbox";

    public static readonly IReadOnlyList<string> All = [Overview, Orders, Refunds, Exceptions, Anomalies, Tasks, Outbox];
}

/// <summary>
/// Turns a database notification payload into signals. The payloads are written by the database's own triggers (see the
/// <c>LiveChangeNotifications</c> migrations) and carry ids and coarse kinds only. A payload that cannot be understood yields nothing
/// and never throws: the listener must survive anything that reaches the channel.
/// </summary>
public static class LiveSignalParser
{
    public const int MaxPayloadLength = 8000; // PostgreSQL's own limit for a notification payload

    public static IReadOnlyList<LiveSignal> Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaxPayloadLength)
            return [];

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("k", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
                return [];

            switch (kindElement.GetString())
            {
                case "res":
                    return Guid(root, "id") is { } reservation ? [LiveSignal.ForReservation(reservation)] : [];

                case "tkt":
                    return Guid(root, "o") is { } ticketOrder ? [LiveSignal.ForOrder(ticketOrder)] : [];

                case "wrk":
                    return Text(root, "t") switch
                    {
                        "reconciliation_tasks" => [LiveSignal.ForAdmin(LiveAreas.Tasks), LiveSignal.ForAdmin(LiveAreas.Overview)],
                        "outbox_events" => [LiveSignal.ForAdmin(LiveAreas.Outbox), LiveSignal.ForAdmin(LiveAreas.Overview)],
                        _ => []
                    };

                case "fin":
                    return Financial(Text(root, "t"), Guid(root, "o"));

                default:
                    return [];
            }
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<LiveSignal> Financial(string? entityType, Guid? orderId)
    {
        var signals = new List<LiveSignal>();
        switch (entityType)
        {
            case "Order":
            case "PaymentAttempt":
                if (orderId is { } o)
                    signals.Add(LiveSignal.ForOrder(o));
                signals.Add(LiveSignal.ForAdmin(LiveAreas.Orders));
                break;

            case "PaymentException":
                // A held order changes what its customer can do next, so the customer is told as well.
                if (orderId is { } held)
                    signals.Add(LiveSignal.ForOrder(held));
                signals.Add(LiveSignal.ForAdmin(LiveAreas.Exceptions));
                break;

            case "Refund":
                signals.Add(LiveSignal.ForAdmin(LiveAreas.Refunds));
                break;

            case "PaymentAnomaly":
                signals.Add(LiveSignal.ForAdmin(LiveAreas.Anomalies));
                break;

            default:
                return [];
        }

        signals.Add(LiveSignal.ForAdmin(LiveAreas.Overview));
        return signals;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Guid? Guid(JsonElement root, string name) =>
        Text(root, name) is { } text && System.Guid.TryParse(text, out var id) && id != System.Guid.Empty ? id : null;
}
