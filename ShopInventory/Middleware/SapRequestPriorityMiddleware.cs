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
/// </remarks>
public sealed class SapRequestPriorityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<SapBackgroundWorkAttribute>() is not null)
        {
            await next(context);
            return;
        }

        using var interactive = SapRequestPriority.BeginInteractive();
        await next(context);
    }
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
