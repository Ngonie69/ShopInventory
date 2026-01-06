using Microsoft.JSInterop;

namespace ShopInventory.Web.Services;

public interface IKeyboardShortcutService : IAsyncDisposable
{
    event Action? OnSearchRequested;
    event Action? OnHelpRequested;
    event Action? OnCreateInvoice;
    event Action? OnGoHome;
    event Action? OnToggleTheme;
    event Action? OnRefresh;
    Task InitializeAsync();
    IReadOnlyList<KeyboardShortcut> GetShortcuts();
}

public class KeyboardShortcut
{
    public string Keys { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}

public class KeyboardShortcutService : IKeyboardShortcutService
{
    private readonly IJSRuntime _jsRuntime;
    private DotNetObjectReference<KeyboardShortcutService>? _dotNetRef;
    private bool _initialized;

    public event Action? OnSearchRequested;
    public event Action? OnHelpRequested;
    public event Action? OnCreateInvoice;
    public event Action? OnGoHome;
    public event Action? OnToggleTheme;
    public event Action? OnRefresh;

    private readonly List<KeyboardShortcut> _shortcuts = new()
    {
        // Navigation
        new() { Keys = "Ctrl + K", Description = "Open global search", Category = "Navigation" },
        new() { Keys = "G then H", Description = "Go to Home/Dashboard", Category = "Navigation" },
        new() { Keys = "G then I", Description = "Go to Invoices", Category = "Navigation" },
        new() { Keys = "G then P", Description = "Go to Products", Category = "Navigation" },
        new() { Keys = "G then R", Description = "Go to Reports", Category = "Navigation" },
        
        // Actions
        new() { Keys = "N then I", Description = "Create new invoice", Category = "Actions" },
        new() { Keys = "Ctrl + Shift + D", Description = "Toggle dark mode", Category = "Actions" },
        new() { Keys = "F5 or Ctrl + R", Description = "Refresh current page", Category = "Actions" },
        
        // Help
        new() { Keys = "?", Description = "Show keyboard shortcuts", Category = "Help" },
        new() { Keys = "Escape", Description = "Close dialogs/modals", Category = "Help" }
    };

    public KeyboardShortcutService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        _dotNetRef = DotNetObjectReference.Create(this);
        await _jsRuntime.InvokeVoidAsync("keyboardShortcuts.initialize", _dotNetRef);
        _initialized = true;
    }

    [JSInvokable]
    public void HandleShortcut(string shortcut)
    {
        switch (shortcut)
        {
            case "search":
                OnSearchRequested?.Invoke();
                break;
            case "help":
                OnHelpRequested?.Invoke();
                break;
            case "createInvoice":
                OnCreateInvoice?.Invoke();
                break;
            case "goHome":
                OnGoHome?.Invoke();
                break;
            case "toggleTheme":
                OnToggleTheme?.Invoke();
                break;
            case "refresh":
                OnRefresh?.Invoke();
                break;
        }
    }

    public IReadOnlyList<KeyboardShortcut> GetShortcuts() => _shortcuts.AsReadOnly();

    public async ValueTask DisposeAsync()
    {
        if (_initialized)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("keyboardShortcuts.dispose");
            }
            catch { }
        }
        _dotNetRef?.Dispose();
    }
}
