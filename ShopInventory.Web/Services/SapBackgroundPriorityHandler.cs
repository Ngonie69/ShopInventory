namespace ShopInventory.Web.Services;

/// <summary>
/// Adds <see cref="SapBackgroundPriority.HeaderName"/> to requests made inside a
/// <see cref="SapBackgroundPriority"/> scope, and holds those requests to
/// <see cref="MaxConcurrentBackgroundRequests"/> in flight across the whole process. Every other
/// request passes untouched and unqueued.
/// </summary>
/// <remarks>
/// The header keeps the Web's cache sweeps out of the SAP slots the API reserves for people, but it
/// does not stop the sweeps arriving together. After a restart the caches are cold, the first users
/// to open pages each start sweeps across four services and every warehouse, and those sweeps all
/// reached the API at once: on 2026-09-18 the catch-up after a cutover queued SAP requests for up to
/// 42.6s and ended in a 503 from the Service Layer. Queuing here spaces them out.
/// <para>
/// Queuing a request rather than a sweep leaves each service's own guards exactly as they were: a
/// second sweep for a warehouse still sees the first one in progress and skips, rather than waiting
/// its turn and then repeating it. A sweep asks for one page at a time, so a request only waits for
/// the handful ahead of it, well inside the clients' timeouts. The one long background call there
/// could be, the Web asking the API to run a full price sync, is started by a person from the Prices
/// page and never inside a background scope, so it cannot hold a slot for its 35 minutes.
/// </para>
/// </remarks>
public sealed class SapBackgroundPriorityHandler : DelegatingHandler
{
    /// <summary>
    /// Background API requests the Web keeps in flight at once. Two of the API's four background SAP
    /// slots, leaving the others to its own posting and sync jobs.
    /// </summary>
    public const int MaxConcurrentBackgroundRequests = 2;

    // Static: the client factory builds a handler per client and rebuilds them as it rotates, so an
    // instance field would give each client its own allowance.
    private static readonly SemaphoreSlim BackgroundRequests =
        new(MaxConcurrentBackgroundRequests, MaxConcurrentBackgroundRequests);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!SapBackgroundPriority.IsBackground)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        if (!request.Headers.Contains(SapBackgroundPriority.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(SapBackgroundPriority.HeaderName, SapBackgroundPriority.BackgroundValue);
        }

        await BackgroundRequests.WaitAsync(cancellationToken);
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            BackgroundRequests.Release();
        }
    }
}
