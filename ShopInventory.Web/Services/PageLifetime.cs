namespace ShopInventory.Web.Services;

/// <summary>
/// The life of the page on screen in this circuit: a token cancelled when the user moves to a
/// different page.
/// </summary>
/// <remarks>
/// <para>
/// <c>PageLifetimeBoundary</c>, in the router, renews it whenever the routed page's component type
/// changes, which is exactly when Blazor disposes the old page. Moving between two routes of one
/// page (<c>/</c> and <c>/dashboard</c>, a query string) keeps the page, and keeps this.
/// </para>
/// <para>
/// A page opts its reads in with <see cref="PageReads.Bind"/>; nothing is cancelled otherwise.
/// </para>
/// </remarks>
public sealed class PageLifetime : IDisposable
{
    private CancellationTokenSource _current = new();

    /// <summary>Cancelled when the user leaves the page that is on screen now.</summary>
    public CancellationToken Token => _current.Token;

    /// <summary>Ends the current page's life and starts the next one's.</summary>
    public void Renew()
    {
        var previous = Interlocked.Exchange(ref _current, new CancellationTokenSource());
        try
        {
            previous.Cancel();
        }
        finally
        {
            previous.Dispose();
        }
    }

    public void Dispose()
    {
        _current.Cancel();
        _current.Dispose();
    }
}
