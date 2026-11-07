using Npgsql;
using NpgsqlTypes;
using PulseReserve.Modules.Payments.Domain;

namespace PulseReserve.Modules.Payments.Persistence.Postgres;

/// <summary>
/// Hands the ambient <see cref="FinancialTransitionContext"/> to the database for the CURRENT transaction (transaction-local
/// settings, gone at commit or rollback), where the financial-transition trigger reads it. Nothing here writes history itself.
/// </summary>
internal static class TransitionSql
{
    public const string ApplyContext =
        """
        SELECT set_config('pulsereserve.source', @source, true), set_config('pulsereserve.fact_basis', @basis, true),
               set_config('pulsereserve.provider_event_id', @event, true), set_config('pulsereserve.actor_user_id', @actor, true),
               set_config('pulsereserve.correlation_id', @correlation, true), set_config('pulsereserve.reason', @reason, true)
        """;

    /// <summary>A rejected or conflicting event: recorded once per provider event, in the same history, without changing any state.</summary>
    public const string InsertRejected =
        """
        INSERT INTO payments.financial_transitions
            (id, occurred_at_utc, entity_type, entity_id, order_id, from_state, to_state, event_code, kind, source, fact_basis,
             provider, provider_event_id, actor_user_id, correlation_id, reason)
        SELECT gen_random_uuid(), now(), 'PaymentAttempt', a.id, a.order_id, a.status, @attempted, 'paymentattempt.rejected_conflicting_verdict',
               'Rejected', NULLIF(@source, ''), NULLIF(@basis, ''), a.provider, NULLIF(@event, ''), NULLIF(@actor, '')::uuid,
               NULLIF(@correlation, ''), 'conflicting_provider_verdict'
        FROM payments.payment_attempts a
        WHERE a.id = @id
        ON CONFLICT (entity_type, entity_id, event_code, provider_event_id) WHERE kind = 'Rejected' AND provider_event_id IS NOT NULL DO NOTHING
        """;

    public static bool HasContext => FinancialTransitionContext.Current is not null;

    public static void Bind(NpgsqlCommand command)
    {
        var info = FinancialTransitionContext.Current;
        Add(command, "source", info?.Source ?? TransitionSource.Unspecified);
        Add(command, "basis", info?.FactBasis ?? FactBasis.Unspecified);
        Add(command, "event", info?.ProviderEventId is { } e ? Truncate(e, 200) : string.Empty);
        Add(command, "actor", info?.ActorUserId?.ToString() ?? string.Empty);
        Add(command, "correlation", info is null ? string.Empty : Truncate(info.CorrelationId, 64));
        Add(command, "reason", info?.Reason is { } r ? Truncate(r, 100) : string.Empty);
    }

    private static void Add(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value });

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
