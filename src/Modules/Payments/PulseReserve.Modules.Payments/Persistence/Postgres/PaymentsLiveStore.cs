using Microsoft.EntityFrameworkCore;
using Npgsql;
using PulseReserve.Modules.Payments.Features.Live;

namespace PulseReserve.Modules.Payments.Persistence.Postgres;

/// <summary>Read-only projection for real-time order updates (see <see cref="IPaymentsLiveStore"/>).</summary>
internal sealed class PaymentsLiveStore(PaymentsDbContext db) : IPaymentsLiveStore
{
    public async Task<OrderLiveSnapshot?> ReadOrderLiveSnapshotAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT o.id, o.customer_id, o.reservation_id,
                   CASE WHEN o.status = 'PendingPayment' AND o.payment_deadline_utc <= now() THEN 'Expired' ELSE o.status END,
                   (SELECT a.status FROM payments.payment_attempts a WHERE a.order_id = o.id ORDER BY a.attempt_number DESC LIMIT 1),
                   (SELECT count(*) FROM payments.tickets t WHERE t.order_id = o.id)
            FROM payments.orders o
            WHERE o.id = @id
            """;
        cmd.Parameters.AddWithValue("id", orderId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new OrderLiveSnapshot(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), (int)reader.GetInt64(5));
    }
}
