using Blazored.LocalStorage;

namespace ShopInventory.Web.Services;

/// <summary>
/// Which groups of the staff sidebar the user has collapsed.
/// </summary>
/// <remarks>
/// Scoped rather than held on <c>NavMenu</c> itself: the shell swaps the
/// persistent drawer for the temporary one at the md breakpoint, which disposes
/// one NavMenu and builds another, and state living on the component would go
/// with it. Only the collapsed ids are stored, so a section added later opens by
/// default rather than inheriting some other section's answer.
/// </remarks>
public sealed class NavSectionState
{
    private const string StorageKey = "kf-nav-collapsed";

    private readonly ILocalStorageService _localStorage;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private bool _loaded;

    public NavSectionState(ILocalStorageService localStorage) => _localStorage = localStorage;

    /// <summary>
    /// Raised when the collapsed set changes for a reason other than a click on
    /// the section itself — which today means the one read from local storage.
    /// </summary>
    public event Action? Changed;

    public bool IsExpanded(string sectionId) => !_collapsed.Contains(sectionId);

    public async Task ToggleAsync(string sectionId)
    {
        if (!_collapsed.Remove(sectionId))
        {
            _collapsed.Add(sectionId);
        }

        await SaveAsync();
    }

    /// <summary>
    /// Reads the stored set, once per circuit. Called after the first render:
    /// prerendering is off, but local storage is still a JS call and there is
    /// nothing to call into until the circuit is up.
    /// </summary>
    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        string[]? stored;
        try
        {
            stored = await _localStorage.GetItemAsync<string[]>(StorageKey);
        }
        catch
        {
            // Storage unavailable, or holding something this build cannot read.
            // Every group opens, which is the default anyway.
            return;
        }

        if (stored is null || stored.Length == 0)
        {
            return;
        }

        foreach (var sectionId in stored)
        {
            _collapsed.Add(sectionId);
        }

        Changed?.Invoke();
    }

    private async Task SaveAsync()
    {
        try
        {
            await _localStorage.SetItemAsync(StorageKey, _collapsed.ToArray());
        }
        catch
        {
            // A sidebar that forgets is better than one that throws.
        }
    }
}
