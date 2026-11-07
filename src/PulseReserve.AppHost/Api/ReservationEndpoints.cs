using Microsoft.AspNetCore.Mvc;
using PulseReserve.AppHost.Security;
using PulseReserve.Modules.Reservation.Domain;
using PulseReserve.Modules.Reservation.Features.Holds;
using PulseReserve.Modules.Reservation.Features.Holds.HoldSeats;
using PulseReserve.Modules.Reservation.Features.Holds.ManageReservation;
using PulseReserve.Modules.Reservation.Features.Inventory;

namespace PulseReserve.AppHost.Api;

/// <summary>Request body for holding seats.</summary>
/// <remarks>
/// There is deliberately no customer field: the owner is always the authenticated principal's user ID (ADR-007).
/// </remarks>
public sealed record HoldSeatsApiRequest(Guid EventId, IReadOnlyList<Guid> SeatIds);

/// <summary>Minimal-API adapter for seat availability and holds. No business logic lives here.</summary>
public static class ReservationEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string IdempotentReplayHeader = "Idempotent-Replayed";

    public static WebApplication MapReservationApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").WithTags("Reservations");

        // Availability is public read-only data (no customer information).
        api.MapGet("/events/{eventId:guid}/availability", async (
            Guid eventId,
            GetEventAvailabilityHandler handler,
            CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(eventId, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblemResult();
        })
        .WithName("GetEventAvailability")
        .Produces<EventAvailabilityResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // Everything below is private to the signed-in customer: 401 without a session, ownership from the session only,
        // and a CSRF token on every POST.
        var mine = api.MapGroup("/reservations")
            .RequireAuthorization(AuthPolicies.Customer)
            .AddEndpointFilter<CsrfValidationFilter>();

        mine.MapPost("/", async (
            [FromBody] HoldSeatsApiRequest request,
            [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
            HoldSeatsHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();

            var attempt = await handler.HandleAsync(
                new HoldSeatsCommand(request.EventId, customerId, request.SeatIds ?? [], idempotencyKey), ct);

            if (attempt.Error is not null)
            {
                return attempt.UnavailableSeatIds.Count > 0
                    ? attempt.Error.ToProblemResult(new Dictionary<string, object?> { ["unavailableSeatIds"] = attempt.UnavailableSeatIds })
                    : attempt.Error.ToProblemResult();
            }

            var reservation = attempt.Reservation!;
            if (attempt.IsReplay)
            {
                http.Response.Headers[IdempotentReplayHeader] = "true";
                return Results.Ok(reservation);
            }

            return Results.Created($"/api/reservations/{reservation.ReservationId}", reservation);
        })
        .WithName("HoldSeats")
        .Produces<ReservationResponse>(StatusCodes.Status201Created)
        .Produces<ReservationResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // The caller's own live hold for an event (a hold made in another browser, or after local state was cleared). "None" is a normal
        // answer, not an error: 204 No Content (a 404 would put a red error in every visitor's console on every event page).
        mine.MapGet("/active", async (
            [FromQuery] Guid eventId,
            ReservationQueriesHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();

            var result = await handler.GetActiveAsync(new GetActiveReservationQuery(eventId, customerId), ct);
            if (result.IsSuccess)
                return Results.Ok(result.Value);

            return result.Error == ReservationErrors.NoActiveHold ? Results.NoContent() : result.Error.ToProblemResult();
        })
        .WithName("GetActiveReservation")
        .Produces<ReservationResponse>()
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        mine.MapGet("/{reservationId:guid}", async (
            Guid reservationId,
            ReservationQueriesHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();

            var result = await handler.GetAsync(new GetReservationQuery(reservationId, customerId), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblemResult();
        })
        .WithName("GetReservation")
        .Produces<ReservationResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        mine.MapPost("/{reservationId:guid}/release", async (
            Guid reservationId,
            ReservationQueriesHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();

            var result = await handler.ReleaseAsync(new ReleaseReservationCommand(reservationId, customerId), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblemResult();
        })
        .WithName("ReleaseReservation")
        .Produces<ReservationResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
