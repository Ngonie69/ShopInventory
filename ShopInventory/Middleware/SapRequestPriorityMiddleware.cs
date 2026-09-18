namespace ShopInventory.Middleware;

/// <summary>
/// Marks the SAP calls an HTTP request makes as interactive, so they can use the slots
/// <see cref="SAPConcurrencyHandler"/> keeps out of reach of background work.
/// </summary>
/// <remarks>
/// Before this, exactly one path in the application opened an interactive scope — sales order
/// approval. Every other request a person waits on (business partner lists, product lookups, stock,
/// invoices, statements) queued first-come-first-served against the same six slots as the price
/// catalog sync and the five- and ten-second posting jobs, which is the shape of "the app is slow
/// but it is fine on an idle system".
///
/// Interactive is the default because that is what an inbound HTTP request almost always is.
/// Endpoints that behave like jobs opt out with <see cref="SapBackgroundWorkAttribute"/>.
///
/// A caller can also opt a single request out with <see cref="HeaderName"/>:
/// <see cref="BackgroundValue"/>. That is for endpoints that serve both kinds of traffic — the
/// Web's cache services walk <c>/paged</c> endpoints page by page in fire-and-forget sweeps, and
/// the same endpoints answer the first page a person is waiting on, so the endpoint cannot be
/// annotated either way. The header can only lower priority: any other value is ignored, and it
/// never lifts an endpoint marked <see cref="SapBackgroundWorkAttribute"/>. Lowering your own
/// priority harms nobody else, which is why it needs no authorisation.
/// </remarks>
public sealed class SapRequestPriorityMiddleware(RequestDelegate next)
{
    /// <summary>The request header a caller sets to declare its request background work.</summary>
    /// <remarks>ShopInventory.Web keeps its own copy in <c>SapBackgroundPriority</c>; a test pins the two together.</remarks>
    public const string HeaderName = "X-Sap-Priority";

    /// <summary>The only <see cref="HeaderName"/> value honoured. There is no value that raises priority.</summary>
    public const string BackgroundValue = "background";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<SapBackgroundWorkAttribute>() is not null
            || CallerDeclaredBackground(context.Request))
        {
            await next(context);
            return;
        }

        using var interactive = SapRequestPriority.BeginInteractive();
        await next(context);
    }

    private static bool CallerDeclaredBackground(HttpRequest request) =>
        request.Headers.TryGetValue(HeaderName, out var values)
        && values.Any(value => string.Equals(value?.Trim(), BackgroundValue, StringComparison.OrdinalIgnoreCase));
}

public static class SapRequestPriorityMiddlewareExtensions
{
    /// <summary>
    /// Add after routing — the endpoint has to be resolved for the opt-out attribute to be visible
    /// — and after output caching, so a cached response does not claim a reservation it will never
    /// use.
    /// </summary>
    public static IApplicationBuilder UseSapRequestPriority(this IApplicationBuilder builder) =>
        builder.UseMiddleware<SapRequestPriorityMiddleware>();
}
