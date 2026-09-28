namespace ShopInventory.Web.Services;

/// <summary>
/// Ties the API reads a page starts to that page's life, so leaving the page cancels them rather
/// than leaving them to run to the five-minute client timeout, holding API and SAP capacity for an
/// answer nobody will see.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, per page: a lifecycle method begins with
/// <c>using var pageReads = PageReads.Bind(PageLifetime);</c>. The binding is an
/// <see cref="AsyncLocal{T}"/>, as <see cref="SapBackgroundPriority"/> is, so it reaches every call
/// that method makes, through services and MediatR, without a token threaded through any of them;
/// <see cref="PageReadCancellationHandler"/> reads it as the request leaves. Only GETs are cancelled,
/// never a save.
/// </para>
/// <para>
/// Most services catch a failed call, log it and return null, so the page may carry on as though
/// the load failed. Two things keep that invisible: a toast raised from a flow whose page has been
/// left is dropped (<see cref="PageAwareSnackbar"/>), and so is a cancellation logged from one.
/// </para>
/// <para>
/// Work shared with other users or pages must not inherit a binding: one person leaving would
/// cancel a load everyone else is waiting on. Such work runs inside <see cref="Detach"/>.
/// </para>
/// </remarks>
public static class PageReads
{
    private static readonly AsyncLocal<StrongBox?> Current = new();

    /// <summary>
    /// Binds the reads this flow makes to the page on screen now. Dispose to end the binding early;
    /// it also ends with the async method that made it.
    /// </summary>
    public static IDisposable Bind(PageLifetime lifetime) => Set(new StrongBox(lifetime.Token));

    /// <summary>Runs what follows as nobody's page: for loads shared across users or pages.</summary>
    public static IDisposable Detach() => Set(null);

    /// <summary>The token of the page this flow is bound to, or none.</summary>
    public static CancellationToken Token => Current.Value?.Token ?? CancellationToken.None;

    /// <summary>True when this flow is bound to a page the user has since left.</summary>
    public static bool HasLeft => Current.Value?.Token.IsCancellationRequested == true;

    private static IDisposable Set(StrongBox? value)
    {
        var previous = Current.Value;
        Current.Value = value;
        return new Restore(previous);
    }

    // A box so that Detach (null) and "never bound" read the same, and a binding is one allocation.
    private sealed record StrongBox(CancellationToken Token);

    private sealed class Restore(StrongBox? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
