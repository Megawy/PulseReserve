using PulseReserve.AppHost.Security;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Providers;

namespace PulseReserve.AppHost.Api;

/// <summary>One payment provider the customer may choose for an order (only enabled providers that can charge the order currency).</summary>
public sealed record PaymentOptionResponse(string Provider, string DisplayName, bool IsSimulated);

/// <summary>The methods offered for one order. When <c>Eligible</c> is false, <c>Reason</c> says why and <c>Providers</c> is empty.</summary>
public sealed record PaymentOptionsResponse(string Currency, IReadOnlyList<PaymentOptionResponse> Providers, bool Eligible = true, string? Reason = null);

/// <summary>
/// Provider webhooks and the customer's list of payment options. Webhooks are anonymous BY NATURE (the provider calls
/// them), so their only protection is the provider signature, verified on the exact raw body before anything is read;
/// they take no cookies and no antiforgery token, and answer with reason codes only (never details).
/// </summary>
public static class PaymentProviderEndpoints
{
    /// <summary>Largest webhook body accepted; provider events are a few KB.</summary>
    public const int MaxWebhookBytes = 256 * 1024;

    // Route segment -> provider name. A whitelist: nothing in the URL selects code or configuration.
    private static readonly Dictionary<string, string> WebhookProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stripe"] = "Stripe",
        ["paymob"] = "Paymob",
    };

    public static WebApplication MapPaymentProviderApi(this WebApplication app)
    {
        app.MapGet("/api/orders/{orderId:guid}/payment-options", async (Guid orderId, PaymentService payments, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } customerId)
                return Results.Unauthorized();
            var result = await payments.OptionsAsync(customerId, orderId, ct);
            return result.IsSuccess
                ? Results.Ok(new PaymentOptionsResponse(
                    result.Value.Currency,
                    result.Value.Providers.Select(p => new PaymentOptionResponse(p.Name, DisplayName(p.Name, p.IsSimulated), p.IsSimulated)).ToList(),
                    result.Value.Eligible, result.Value.Reason))
                : result.Error.ToProblemResult();
        })
        .RequireAuthorization(AuthPolicies.Customer)
        .WithTags("Orders")
        .WithName("GetPaymentOptions")
        .Produces<PaymentOptionsResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/api/webhooks/{provider}", async (string provider, HttpRequest request, PaymentWebhookService webhooks, CancellationToken ct) =>
        {
            if (!WebhookProviders.TryGetValue(provider, out var providerName))
                return Results.NotFound();
            if (request.ContentLength is > MaxWebhookBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            // The signature covers the exact bytes: read the body untouched, bounded.
            string body;
            using (var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true))
            {
                var buffer = new char[MaxWebhookBytes + 1];
                var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                if (read > MaxWebhookBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                body = new string(buffer, 0, read);
            }

            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var query = WebhookRequestRedaction.ReadQuery(request.HttpContext);
            var result = await webhooks.HandleAsync(providerName, new WebhookRequest(body, headers, query), ct);

            // The status is what the provider acts on, so it is chosen by one rule: 2xx only when the event needs no redelivery (it was
            // applied, is a harmless duplicate, or its evidence is DURABLY recorded); 4xx when it is not authentic or not readable;
            // 5xx when it could not be recorded or judged, so the provider delivers it again. Bodies carry reason codes only.
            return result.Outcome switch
            {
                WebhookOutcome.Applied or WebhookOutcome.Acknowledged => Results.Ok(new { status = result.Reason }),
                WebhookOutcome.AnomalyRecorded => Results.Ok(new { status = "recorded_for_review", reason = result.Reason }),
                WebhookOutcome.ProviderUnavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                WebhookOutcome.Failed => Results.Json(new { status = "error", reason = result.Reason }, statusCode: StatusCodes.Status500InternalServerError),
                WebhookOutcome.Rejected => Results.Json(new { status = "rejected", reason = result.Reason }, statusCode: StatusCodes.Status400BadRequest),
                // Fail closed: an outcome nobody mapped must never turn into a 2xx (acknowledged) or a silent 4xx (retried as "not ours").
                // A 5xx is not an acknowledgement, and the exception is logged by the host.
                _ => throw new InvalidOperationException($"The webhook outcome '{result.Outcome}' has no HTTP mapping.")
            };
        })
        .AllowAnonymous()
        .WithTags("Payment webhooks")
        .WithName("ReceivePaymentWebhook")
        .ExcludeFromDescription();

        return app;
    }

    private static string DisplayName(string provider, bool simulated) => provider switch
    {
        "Stripe" => "Card payment (Stripe)",
        "Paymob" => "Card or wallet payment (Paymob)",
        _ when simulated => "Payment simulator",
        _ => provider
    };
}
