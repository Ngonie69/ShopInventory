using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace ShopInventory.Web.Services;

/// <summary>
/// MudBlazor's snackbar, except that a toast raised by code still running for a page the user has
/// left is dropped.
/// </summary>
/// <remarks>
/// The snackbar lives in the layout and outlasts every page. When a page's read is cancelled because
/// the user moved on (<see cref="PageReads"/>), the page's own error handling can still run, and a
/// "failed to load" it raises would appear over the page the user went to. The flow that raises it is
/// bound to the page it came from, so <see cref="PageReads.HasLeft"/> says exactly which toasts those
/// are; everything else passes through.
/// </remarks>
public sealed class PageAwareSnackbar(ISnackbar inner) : ISnackbar
{
    public IEnumerable<Snackbar> ShownSnackbars => inner.ShownSnackbars;

    public SnackbarConfiguration Configuration => inner.Configuration;

    public event Action? OnSnackbarsUpdated
    {
        add => inner.OnSnackbarsUpdated += value;
        remove => inner.OnSnackbarsUpdated -= value;
    }

    public Snackbar? Add(string message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null) =>
        PageReads.HasLeft ? null : inner.Add(message, severity, configure, key);

    public Snackbar? Add(MarkupString message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null) =>
        PageReads.HasLeft ? null : inner.Add(message, severity, configure, key);

    public Snackbar? Add(RenderFragment message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null) =>
        PageReads.HasLeft ? null : inner.Add(message, severity, configure, key);

    public Snackbar? Add<T>(Dictionary<string, object>? componentParameters = null, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null)
        where T : IComponent =>
        PageReads.HasLeft ? null : inner.Add<T>(componentParameters, severity, configure, key);

    public void Clear() => inner.Clear();

    public void Remove(Snackbar snackbar) => inner.Remove(snackbar);

    public void RemoveByKey(string key) => inner.RemoveByKey(key);

    public void Dispose() => inner.Dispose();
}
