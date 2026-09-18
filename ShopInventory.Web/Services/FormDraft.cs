using System.Security.Claims;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.JSInterop;

namespace ShopInventory.Web.Services;

/// <summary>
/// A document form's unsaved entries, as kept in the browser's localStorage.
/// </summary>
/// <remarks>
/// A Blazor Server page keeps its state in the circuit. When the connection drops long enough for the
/// server to evict the circuit — a deploy, an app-pool recycle, a laptop lid, a flaky link — the
/// reconnect modal can only reload the page, and a half-entered document went with it. Each create
/// form now writes its entries to the browser after every change and reads them back on the next load.
///
/// A form's draft carries its idempotency key where the form has one. If the circuit died while a
/// submit was in flight the document may already exist; resubmitting the restored draft sends the
/// same key, so the API replays that document instead of creating a second.
///
/// The key is per form and per user, so a shared PC never hands one operator's entries to the next,
/// and a draft older than <see cref="DefaultMaxAge"/> is dropped rather than resurfacing on the next shift.
/// </remarks>
public static class FormDraft
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(12);

    private const string KeyPrefix = "form-draft:";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The localStorage key for one form and one user, or null when there is no user to scope it to.</summary>
    public static string? StorageKey(string form, string? userName)
        => string.IsNullOrWhiteSpace(userName) ? null : $"{KeyPrefix}{form}:{userName.Trim().ToLowerInvariant()}";

    /// <summary>The user a draft is scoped to: the signed-in user's name, or null when nobody is signed in.</summary>
    public static string? UserName(ClaimsPrincipal? user)
        => user?.Identity?.IsAuthenticated == true ? user.Identity.Name : null;

    /// <summary>
    /// The JSON compared against what was last written. An unchanged form produces the same string on
    /// every render, so nothing is written.
    /// </summary>
    public static string Fingerprint<TState>(TState state) => JsonSerializer.Serialize(state, JsonOptions);

    public static string Serialize<TState>(TState state, DateTimeOffset savedAt)
        => JsonSerializer.Serialize(new Stored<TState> { SavedAt = savedAt, State = state }, JsonOptions);

    /// <summary>
    /// Reads a stored draft back. Null when there is none, it cannot be read, or it is older than
    /// <paramref name="maxAge"/> at <paramref name="now"/>. A draft stamped well in the future (a
    /// clock that jumped) is treated as unreadable too.
    /// </summary>
    public static Restored<TState>? TryRestore<TState>(string? json, DateTimeOffset now, TimeSpan maxAge)
        where TState : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        Stored<TState>? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Stored<TState>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        if (stored?.State is null || now - stored.SavedAt > maxAge || stored.SavedAt > now.AddMinutes(5))
        {
            return null;
        }

        return new Restored<TState>(stored.State, stored.SavedAt);
    }

    /// <summary>The sentence a form shows once it has put a draft back.</summary>
    public static string RestoredNotice(string document, int lineCount, DateTimeOffset savedAt)
    {
        var lines = lineCount switch
        {
            0 => string.Empty,
            1 => "1 line, ",
            _ => $"{lineCount} lines, "
        };

        return $"Restored the {document} you were entering ({lines}last changed {savedAt.ToLocalTime():HH:mm}). "
            + "Check it before you submit it.";
    }

    public sealed record Restored<TState>(TState State, DateTimeOffset SavedAt);

    private sealed class Stored<TState>
    {
        public DateTimeOffset SavedAt { get; set; }

        public TState? State { get; set; }
    }
}

/// <summary>
/// Reads and writes one form's draft for the signed-in user. A page makes one on first render, calls
/// <see cref="RestoreAsync"/> once master data is in, and <see cref="SaveAsync"/> after every later render.
/// </summary>
/// <remarks>
/// Storage failures are logged and swallowed: a page must keep working when the circuit is going away
/// or the browser refuses storage, and a save that failed is retried on the next render because its
/// fingerprint was not recorded.
/// </remarks>
public sealed class FormDraftStore<TState> where TState : class
{
    private readonly ILocalStorageService _storage;
    private readonly ILogger _logger;
    private readonly string? _key;
    private readonly TimeSpan _maxAge;
    private string? _lastFingerprint;
    private bool _restored;

    public FormDraftStore(
        ILocalStorageService storage,
        ILogger logger,
        string form,
        ClaimsPrincipal? user,
        TimeSpan? maxAge = null)
    {
        _storage = storage;
        _logger = logger;
        _key = FormDraft.StorageKey(form, FormDraft.UserName(user));
        _maxAge = maxAge ?? FormDraft.DefaultMaxAge;
    }

    /// <summary>
    /// Reads the stored draft. Null when there is nothing to bring back. Either way the page's state
    /// afterwards must be passed to <see cref="Accept"/>, so an untouched page never overwrites a draft
    /// another tab is still writing.
    /// </summary>
    public async Task<FormDraft.Restored<TState>?> RestoreAsync()
    {
        _restored = true;

        if (_key is null)
        {
            return null;
        }

        try
        {
            return FormDraft.TryRestore<TState>(await _storage.GetItemAsStringAsync(_key), DateTimeOffset.UtcNow, _maxAge);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _logger.LogWarning(ex, "Could not read the form draft {Key}", _key);
            return null;
        }
    }

    /// <summary>Records <paramref name="state"/> as what storage already holds.</summary>
    public void Accept(TState state) => _lastFingerprint = FormDraft.Fingerprint(state);

    /// <summary>
    /// Writes <paramref name="state"/> when it differs from what was last written, or removes the
    /// draft when <paramref name="isEmpty"/>. Does nothing before <see cref="RestoreAsync"/> has run,
    /// so the renders that happen while master data loads cannot clear a draft still to be restored.
    /// </summary>
    public async Task SaveAsync(TState state, bool isEmpty)
    {
        if (_key is null || !_restored)
        {
            return;
        }

        var fingerprint = FormDraft.Fingerprint(state);
        if (fingerprint == _lastFingerprint)
        {
            return;
        }

        try
        {
            if (isEmpty)
            {
                await _storage.RemoveItemAsync(_key);
            }
            else
            {
                await _storage.SetItemAsStringAsync(_key, FormDraft.Serialize(state, DateTimeOffset.UtcNow));
            }

            _lastFingerprint = fingerprint;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _logger.LogDebug(ex, "Could not save the form draft {Key}", _key);
        }
    }

    private static bool IsStorageFailure(Exception ex)
        => ex is JSDisconnectedException or JSException or TaskCanceledException or InvalidOperationException;
}
