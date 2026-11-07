using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PulseReserve.AppHost.Security;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;

namespace PulseReserve.AppHost.Api;

/// <summary>Body of a manual refund record. Nullable so that "missing" is distinguishable from "false"/zero.</summary>
public sealed record ManualRefundApiRequest(bool? ConfirmedPerformedExternally, decimal? Amount, string? Currency, string? ExternalReference, string? Note);

/// <summary>
/// Trusted operational interface for payment reconciliation, refunds and the outbox. Admin policy and CSRF protection
/// on everything; every state change records who did it in an append-only audit table. There is deliberately no
/// customer-facing way to start, retry or inspect any of this.
/// </summary>
/// <summary>Body of a payment-exception resolution. Nullable so that "missing" is distinguishable from "false"/zero.</summary>
public sealed record ResolvePaymentExceptionApiRequest(bool? RefundPerformedExternally, decimal? Amount, string? Currency, string? ExternalReference, string? Note);

/// <summary>Body of an anomaly resolution. Nullable so that "missing" is distinguishable from "false".</summary>
public sealed record ResolvePaymentAnomalyApiRequest(bool? InvestigationConfirmed, string? Resolution, string? ExternalReference, string? Note);

public static class PaymentOperationsEndpoints
{
    private static readonly HashSet<string> HistoryEntityTypes = ["Order", "PaymentAttempt", "Refund", "PaymentException", "PaymentAnomaly"];

    private static readonly HashSet<string> ExceptionStates = [PaymentExceptionState.Open, PaymentExceptionState.Resolved];

    private static readonly HashSet<string> WorkStates = [WorkState.Pending, WorkState.Processing, WorkState.Done, WorkState.NeedsReview];

    private static readonly HashSet<string> OrderStates =
        [OrderStatus.PendingPayment, OrderStatus.Paid, OrderStatus.Cancelled, OrderStatus.Expired, OrderStatus.RefundRequired, OrderStatus.Refunded];

    private static readonly HashSet<string> RefundStates =
        [RefundState.Requested, RefundState.Unknown, RefundState.Pending, RefundState.Succeeded, RefundState.Failed, RefundState.NeedsReview, RefundState.ManuallyRecorded];

    public static WebApplication MapPaymentOperationsApi(this WebApplication app)
    {
        var ops = app.MapGroup("/api/admin/payments").WithTags("Payment operations (admin)")
            .RequireAuthorization(AuthPolicies.Admin)
            .AddEndpointFilter<CsrfValidationFilter>();

        ops.MapGet("/overview", async (PaymentOperationsService service, CancellationToken ct) =>
            Results.Ok(await service.SummaryAsync(ct))).WithName("PaymentOperationsOverview");

        // Which providers exist and what the system can do with each: lets the console explain states that need a person.
        ops.MapGet("/providers", (PaymentOperationsService service) => Results.Ok(service.Providers())).WithName("ListPaymentProviders");

        ops.MapGet("/reconciliation-tasks", async (string? state, int? limit, PaymentOperationsService service, CancellationToken ct) =>
            state is not null && !WorkStates.Contains(state)
                ? BadState()
                : Results.Ok(await service.TasksAsync(state, limit ?? 50, ct))).WithName("ListReconciliationTasks");

        ops.MapGet("/refunds", async (string? state, int? limit, PaymentOperationsService service, CancellationToken ct) =>
            state is not null && !RefundStates.Contains(state)
                ? BadState()
                : Results.Ok(await service.RefundsAsync(state, limit ?? 50, ct))).WithName("ListRefunds");

        ops.MapGet("/outbox-events", async (string? state, int? limit, PaymentOperationsService service, CancellationToken ct) =>
            state is not null && !WorkStates.Contains(state)
                ? BadState()
                : Results.Ok(await service.OutboxAsync(state, limit ?? 50, ct))).WithName("ListOutboxEvents");

        // Verified provider successes that contradict a recorded Failed verdict and wait for a person.
        ops.MapGet("/exceptions", async (string? state, int? limit, PaymentOperationsService service, CancellationToken ct) =>
            state is not null && !ExceptionStates.Contains(state)
                ? BadState()
                : Results.Ok(await service.ExceptionsAsync(state, limit ?? 50, ct))).WithName("ListPaymentExceptions");

        // Verified-signature success events that were NOT applied because they disagree with our records.
        ops.MapGet("/anomalies", async (string? state, int? limit, PaymentOperationsService service, CancellationToken ct) =>
            state is not null && !ExceptionStates.Contains(state)
                ? BadState()
                : Results.Ok(await service.AnomaliesAsync(state, limit ?? 50, ct))).WithName("ListPaymentAnomalies");

        ops.MapGet("/orders", async (string? status, int? limit, PaymentOperationsService service, CancellationToken ct) =>
            status is not null && !OrderStates.Contains(status)
                ? BadState()
                : Results.Ok(await service.OrdersAsync(status, limit ?? 50, ct))).WithName("ListOperationsOrders");

        ops.MapGet("/orders/{orderId:guid}", async (Guid orderId, PaymentOperationsService service, CancellationToken ct) =>
            await service.OrderAsync(orderId, ct) is { } order
                ? Results.Ok(order)
                : PaymentsErrors.OrderNotFound.ToProblemResult()).WithName("GetOperationsOrder");

        // The append-only history of every financial state change (and every rejected conflicting event): who or what moved what,
        // from which state to which, on what basis. Filter by order, entity type or entity; newest first.
        ops.MapGet("/history", async (Guid? orderId, string? entityType, Guid? entityId, int? limit, PaymentOperationsService service, CancellationToken ct) =>
            entityType is not null && !HistoryEntityTypes.Contains(entityType)
                ? BadState()
                : Results.Ok(await service.HistoryAsync(orderId, entityType, entityId, limit ?? 50, ct))).WithName("ListFinancialHistory");

        ops.MapGet("/audit-log", async (int? limit, PaymentOperationsService service, CancellationToken ct) =>
            Results.Ok(await service.AuditAsync(limit ?? 50, ct))).WithName("ListPaymentAuditLog");

        ops.MapPost("/reconciliation-tasks/{id:guid}/retry", async (Guid id, PaymentOperationsService service, HttpContext http, CancellationToken ct) =>
            await Run(http, admin => service.RetryTaskAsync(id, admin, ct))).RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit).WithName("RetryReconciliationTask");

        ops.MapPost("/outbox-events/{id:guid}/retry", async (Guid id, PaymentOperationsService service, HttpContext http, CancellationToken ct) =>
            await Run(http, admin => service.RetryOutboxAsync(id, admin, ct))).RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit).WithName("RetryOutboxEvent");

        ops.MapPost("/refunds/{id:guid}/retry", async (Guid id, PaymentOperationsService service, HttpContext http, CancellationToken ct) =>
            await Run(http, admin => service.RetryRefundAsync(id, admin, ct))).RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit).WithName("RetryRefund");

        // Records that an administrator performed a refund in an external dashboard (providers that cannot refund through the API).
        // Everything that matters (provider, order, amount, currency, state) is decided from the stored refund; the body only
        // carries the explicit confirmation, the amount/currency the console showed (verified, not trusted) and optional text.
        ops.MapPost("/refunds/{id:guid}/manual-completion", async (
            Guid id, [FromBody] ManualRefundApiRequest? request, PaymentOperationsService service, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } admin)
                return Results.Unauthorized();
            if (request is null || request.Amount is null || request.Currency is null)
                return PaymentsErrors.ManualRefundInvalid.ToProblemResult();
            var result = await service.RecordManualRefundAsync(
                id, new ManualRefundRecord(request.ConfirmedPerformedExternally == true, request.Amount.Value, request.Currency, request.ExternalReference, request.Note), admin, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblemResult();
        }).RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit).WithName("RecordManualRefund");

        // An administrator's statement about the outcome of investigating an anomaly. The resolution code comes from a fixed set; the
        // anomaly itself (provider, amounts, association) is read from the stored row, never from this body.
        ops.MapPost("/anomalies/{id:guid}/resolve", async (
            Guid id, [FromBody] ResolvePaymentAnomalyApiRequest? request, PaymentOperationsService service, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } admin)
                return Results.Unauthorized();
            if (request is null || request.Resolution is null)
                return PaymentsErrors.AnomalyResolutionInvalid.ToProblemResult();
            var result = await service.ResolveAnomalyAsync(
                id, new PaymentAnomalyResolutionRecord(request.InvestigationConfirmed == true, request.Resolution, request.ExternalReference, request.Note), admin, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblemResult();
        }).RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit).WithName("ResolvePaymentAnomaly");

        // An administrator's statement that the money for an exception was returned outside this system. Provider, order, amount
        // and currency are read from the stored exception; the body carries the explicit confirmation, an echo of the amount and
        // currency the console showed (verified, never trusted) and optional text.
        ops.MapPost("/exceptions/{id:guid}/resolve", async (
            Guid id, [FromBody] ResolvePaymentExceptionApiRequest? request, PaymentOperationsService service, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } admin)
                return Results.Unauthorized();
            if (request is null || request.Amount is null || request.Currency is null)
                return PaymentsErrors.ExceptionResolutionInvalid.ToProblemResult();
            var result = await service.ResolveExceptionAsync(
                id, new PaymentExceptionResolutionRecord(request.RefundPerformedExternally == true, request.Amount.Value, request.Currency, request.ExternalReference, request.Note), admin, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblemResult();
        }).RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit).WithName("ResolvePaymentException");

        // Manual initiation: only for an order that already requires a refund (a late success the seats could not honour).
        ops.MapPost("/orders/{orderId:guid}/refund", async (Guid orderId, PaymentOperationsService service, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } admin)
                return Results.Unauthorized();
            var result = await service.RequestRefundAsync(orderId, admin, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblemResult();
        }).RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit).WithName("RequestRefund");

        // Runs one pass of reconciliation, refunds and the outbox now (the background worker does the same on a timer).
        ops.MapPost("/process-now", async (PaymentsWorkflow workflow, HttpContext http, CancellationToken ct) =>
        {
            // The pass is the worker's, started by this administrator: the history keeps both facts.
            using var scope = FinancialTransitionContext.Begin(
                TransitionSource.Administrator, FactBasis.AdministratorAction, actorUserId: http.User.GetUserId(), reason: "process_now");
            return Results.Ok(await workflow.RunOnceAsync(ct));
        }).RequireRateLimiting(AuthPolicies.FinancialWriteRateLimit).WithName("ProcessPaymentWorkNow");

        return app;
    }

    private static async Task<IResult> Run(HttpContext http, Func<Guid, Task<PulseReserve.SharedKernel.Result>> action)
    {
        if (http.User.GetUserId() is not { } admin)
            return Results.Unauthorized();
        var result = await action(admin);
        return result.IsSuccess ? Results.NoContent() : result.Error.ToProblemResult();
    }

    private static IResult BadState() =>
        Results.Problem(title: "Operations.InvalidFilter", detail: "Unknown state filter.", statusCode: StatusCodes.Status400BadRequest);
}
