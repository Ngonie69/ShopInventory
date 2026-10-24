namespace ShopInventory.Web.Models;

/// <summary>
/// Lets only the newest load of a report land on screen.
/// </summary>
/// <remarks>
/// The report pages reload as soon as a route or rep is picked, while the dates wait for Run, so
/// two loads can be in flight at once — and whichever answered last used to win, which is not
/// always the one the filters now describe. <see cref="Begin"/> cancels the load before it.
/// </remarks>
public sealed class LatestLoad : IDisposable
{
    private CancellationTokenSource? _current;

    /// <summary>Starts a load, cancelling any still running, and returns its token.</summary>
    public CancellationToken Begin()
    {
        _current?.Cancel();
        _current?.Dispose();
        _current = new CancellationTokenSource();
        return _current.Token;
    }

    public void Dispose()
    {
        _current?.Cancel();
        _current?.Dispose();
        _current = null;
    }
}
