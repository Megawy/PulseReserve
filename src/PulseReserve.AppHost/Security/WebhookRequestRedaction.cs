using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PulseReserve.AppHost.Security;

/// <summary>
/// Keeps webhook query strings out of logs, traces and diagnostics.
/// <para>
/// Paymob delivers its signature as a query parameter (<c>/api/webhooks/paymob?hmac=...</c>). ASP.NET Core's hosting layer
/// logs every request as "Request starting ... {path}{query}" (category <c>Microsoft.AspNetCore.Hosting.Diagnostics</c>,
/// Information) BEFORE any middleware runs, so no middleware can hide it, and it also tags the request activity. In
/// Development that category is at Information; in production it is at Warning by default but is routinely raised while
/// diagnosing. A signature is not a long-lived key, yet it is a valid proof for that exact payload and must not sit in log
/// stores, so it is treated as sensitive.
/// </para>
/// <para>
/// Scope is deliberately narrow: only requests under <c>/api/webhooks</c>. The query is moved out of
/// <see cref="HttpRequest.QueryString"/> at the moment the request context is created (which happens before the hosting
/// layer logs) into <c>HttpContext.Items</c>, where only the webhook endpoint reads it
/// (<see cref="ReadQuery"/>). Every other request, and all other log lines, are untouched. Request bodies and the
/// <c>Stripe-Signature</c> header are not logged by this application at all.
/// </para>
/// </summary>
public static class WebhookRequestRedaction
{
    public const string PathPrefix = "/api/webhooks";
    public const string QueryItemKey = "pulsereserve.webhook.query";

    public static IServiceCollection AddWebhookQueryRedaction(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IHttpContextFactory>(sp => new RedactingHttpContextFactory(new DefaultHttpContextFactory(sp))));
        return services;
    }

    public static bool IsWebhookPath(PathString path) => path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The webhook's query parameters, wherever they are: the redacted copy if redaction moved them, else the request's.</summary>
    public static IReadOnlyDictionary<string, string> ReadQuery(HttpContext http)
    {
        var raw = http.Items.TryGetValue(QueryItemKey, out var stored) && stored is string s ? s : http.Request.QueryString.Value;
        return string.IsNullOrEmpty(raw)
            ? new Dictionary<string, string>()
            : QueryHelpers.ParseQuery(raw).ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RedactingHttpContextFactory(IHttpContextFactory inner) : IHttpContextFactory
    {
        public HttpContext Create(IFeatureCollection featureCollection)
        {
            var context = inner.Create(featureCollection);
            if (IsWebhookPath(context.Request.Path) && context.Request.QueryString.HasValue)
            {
                context.Items[QueryItemKey] = context.Request.QueryString.Value;
                context.Request.QueryString = QueryString.Empty;
            }

            return context;
        }

        public void Dispose(HttpContext httpContext) => inner.Dispose(httpContext);
    }
}
