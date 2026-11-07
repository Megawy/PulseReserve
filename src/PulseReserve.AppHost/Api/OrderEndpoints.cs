using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PulseReserve.AppHost.Configuration;
using PulseReserve.AppHost.Security;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;

namespace PulseReserve.AppHost.Api;

/// <summary>Checkout request. The body names only the reservation: lines, prices and totals are always server-side.</summary>
public sealed record CreateOrderApiRequest(Guid ReservationId);

/// <summary>Starting a payment names at most the PROVIDER. Amount, currency and everything else come from the persisted order.</summary>
public sealed record StartPaymentApiRequest(string? Provider);

/// <summary>A (simulated) provider verdict. Order, attempt and the unguessable token must all match the stored attempt.</summary>
public sealed record SimulatorCompleteApiRequest(Guid OrderId, Guid AttemptId, string? SimulatorToken, string? Outcome);

/// <summary>A purchased seat's ticket. <c>TicketId</c> is the unguessable public identifier (never an internal key).</summary>
public sealed record TicketResponse(string TicketId, Guid SeatId, Guid EventId, string Status, DateTime IssuedAtUtc);

public sealed record OrderLineResponse(Guid SeatId, int Quantity, decimal UnitPrice, decimal LineTotal);

/// <summary>
/// <c>SimulatorToken</c> is present only for an in-flight attempt of the simulator and only for the owner.
/// <c>CheckoutUrl</c> is the provider-hosted payment page, present only while a real provider's attempt is in flight and
/// only for the owner. No provider secret or key is ever included.
/// </summary>
public sealed record PaymentAttemptResponse(
    Guid AttemptId, int AttemptNumber, string Status, string Provider, bool IsSimulated, string? SimulatorToken,
    DateTime CreatedAtUtc, DateTime? CompletedAtUtc, string? CheckoutUrl = null);

public sealed record OrderResponse(
    Guid OrderId, Guid ReservationId, Guid EventId, string Status, string Currency, decimal TotalAmount,
    DateTime CreatedAtUtc, DateTime PaymentDeadlineUtc, IReadOnlyList<OrderLineResponse> Lines, PaymentAttemptResponse? Payment);

/// <summary>Minimal-API adapter for checkout, orders and payments. No business logic lives here.</summary>
public static class OrderEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static WebApplication MapOrderApi(this WebApplication app, PaymentsOptions payments)
    {
        // Everything is private to the signed-in customer: 401 without a session, ownership from the session only
        // (a foreign order is a 404), and an antiforgery token on every POST.
        var orders = app.MapGroup("/api/orders").WithTags("Orders")
            .RequireAuthorization(AuthPolicies.Customer)
            .AddEndpointFilter<CsrfValidationFilter>();

        orders.MapPost("/", async (
            [FromBody] CreateOrderApiRequest request,
            [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
            CheckoutService checkout,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();

            var result = await checkout.CreateOrderAsync(customerId, request.ReservationId, idempotencyKey, ct);
            if (result.IsFailure)
                return result.Error.ToProblemResult();

            var body = ToResponse(result.Value.Order);
            if (result.Value.IsReplay)
            {
                http.Response.Headers["Idempotent-Replayed"] = "true";
                return Results.Ok(body);
            }

            return Results.Created($"/api/orders/{body.OrderId}", body);
        })
        .RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit)
        .WithName("CreateOrder")
        .Produces<OrderResponse>(StatusCodes.Status201Created)
        .Produces<OrderResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // The signed-in customer's own order history (newest first).
        orders.MapGet("/", async (int? limit, CheckoutService checkout, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();
            var list = await checkout.ListAsync(customerId, limit ?? 50, ct);
            return Results.Ok(list.Select(o => ToResponse(o)).ToList());
        })
        .WithName("ListMyOrders")
        .Produces<List<OrderResponse>>();

        orders.MapGet("/{orderId:guid}", async (Guid orderId, CheckoutService checkout, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();
            var order = await checkout.GetAsync(orderId, customerId, ct);
            return order is null ? PaymentsErrors.OrderNotFound.ToProblemResult() : Results.Ok(ToResponse(order));
        })
        .WithName("GetOrder")
        .Produces<OrderResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        orders.MapGet("/by-reservation/{reservationId:guid}", async (Guid reservationId, CheckoutService checkout, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();
            var order = await checkout.GetByReservationAsync(reservationId, customerId, ct);
            return order is null ? PaymentsErrors.OrderNotFound.ToProblemResult() : Results.Ok(ToResponse(order));
        })
        .WithName("GetOrderByReservation")
        .Produces<OrderResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        // Tickets appear shortly after payment (the outbox delivers them); an empty list means "not issued yet".
        orders.MapGet("/{orderId:guid}/tickets", async (Guid orderId, CheckoutService checkout, IWorkflowStore workflow, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();
            if (await checkout.GetAsync(orderId, customerId, ct) is null)
                return PaymentsErrors.OrderNotFound.ToProblemResult();
            var tickets = await workflow.ListTicketsAsync(orderId, customerId, ct);
            return Results.Ok(tickets.Select(t => new TicketResponse(t.PublicId, t.SeatId, t.EventId, t.Status, t.IssuedAtUtc)).ToList());
        })
        .WithName("GetOrderTickets")
        .Produces<List<TicketResponse>>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        orders.MapPost("/{orderId:guid}/cancel", async (Guid orderId, CheckoutService checkout, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();
            var result = await checkout.CancelAsync(orderId, customerId, ct);
            return result.IsSuccess ? Results.Ok(ToResponse(result.Value)) : result.Error.ToProblemResult();
        })
        .RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit)
        .WithName("CancelOrder")
        .Produces<OrderResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        orders.MapPost("/{orderId:guid}/payments", async (
            Guid orderId,
            [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
            [FromBody] StartPaymentApiRequest? request,
            PaymentService payment,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();

            var result = await payment.InitiateAsync(customerId, orderId, idempotencyKey, request?.Provider, ct);
            if (result.IsFailure)
                return result.Error.ToProblemResult();

            var body = ToResponse(result.Value.Order, result.Value.Attempt);
            if (result.Value.IsReplay)
            {
                http.Response.Headers["Idempotent-Replayed"] = "true";
                return Results.Ok(body);
            }

            return Results.Created($"/api/orders/{orderId}", body);
        })
        .RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit)
        .WithName("StartPayment")
        .Produces<OrderResponse>(StatusCodes.Status201Created)
        .Produces<OrderResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        if (payments.SimulatorEnabled)
            MapSimulator(app);

        return app;
    }

    // DEVELOPMENT/TEST ONLY. Not mapped at all unless Payments:Simulator:Enabled is true, and that setting cannot be
    // true in production (see PaymentsOptions). It stands in for a provider webhook, but additionally requires the
    // signed-in owner of the order, so nobody can complete someone else's payment.
    private static void MapSimulator(WebApplication app)
    {
        app.MapPost("/api/dev/payment-simulator/complete", async (
            [FromBody] SimulatorCompleteApiRequest request,
            CheckoutService checkout,
            PaymentService payment,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();

            using var scope = FinancialTransitionContext.Begin(
                TransitionSource.Simulator, FactBasis.ApplicationDecision, "Simulator", actorUserId: customerId, reason: TransitionReason.ProviderVerdict);
            var succeeded = string.Equals(request.Outcome, "succeed", StringComparison.OrdinalIgnoreCase);
            if (!succeeded && !string.Equals(request.Outcome, "fail", StringComparison.OrdinalIgnoreCase))
                return Results.Problem(title: "Payment.InvalidOutcome", detail: "Outcome must be 'succeed' or 'fail'.", statusCode: StatusCodes.Status400BadRequest);

            // The caller must own the order they name; anything else looks exactly like a missing attempt.
            if (await checkout.GetAsync(request.OrderId, customerId, ct) is null)
                return PaymentsErrors.AttemptNotFound.ToProblemResult();

            var result = await payment.CompleteAsync(
                new PaymentCallback(request.AttemptId, request.OrderId, request.SimulatorToken ?? string.Empty, succeeded,
                    succeeded ? null : "simulated_failure"), ct);
            return result.IsSuccess ? Results.Ok(ToResponse(result.Value)) : result.Error.ToProblemResult();
        })
        .RequireAuthorization(AuthPolicies.Customer)
        .AddEndpointFilter<CsrfValidationFilter>()
        .WithTags("Payment simulator (development only)")
        .RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit)
        .WithName("SimulatorCompletePayment")
        .Produces<OrderResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static OrderResponse ToResponse(Order order, PaymentAttempt? attempt = null)
    {
        var payment = attempt ?? order.Payment;
        return new OrderResponse(
            order.OrderId, order.ReservationId, order.EventId, order.Status, order.Currency, order.TotalAmount,
            order.CreatedAtUtc, order.PaymentDeadlineUtc,
            order.Lines.Select(l => new OrderLineResponse(l.SeatId, l.Quantity, l.UnitPrice, l.LineTotal)).ToList(),
            payment is null
                ? null
                : new PaymentAttemptResponse(
                    payment.AttemptId, payment.AttemptNumber, payment.Status, payment.Provider, payment.IsSimulated,
                    payment.IsSimulated && payment.Status == PaymentAttemptStatus.Initiated ? payment.ProviderReference : null,
                    payment.CreatedAtUtc, payment.CompletedAtUtc,
                    CheckoutUrlPolicy.Visible(order.Status, payment)));
    }
}
