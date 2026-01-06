using Blazored.LocalStorage;

namespace ShopInventory.Web.Services;

public interface IThemeService
{
    event Action? OnThemeChanged;
    Task<string> GetThemeAsync();
    Task SetThemeAsync(string theme);
    Task ToggleThemeAsync();
    bool IsDarkMode { get; }
}

public class ThemeService : IThemeService
{
    private readonly ILocalStorageService _localStorage;
    private const string ThemeKey = "app-theme";
    private string _currentTheme = "light";

    public event Action? OnThemeChanged;
    public bool IsDarkMode => _currentTheme == "dark";

    public ThemeService(ILocalStorageService localStorage)
    {
        _localStorage = localStorage;
    }

    public async Task<string> GetThemeAsync()
    {
        try
        {
            var theme = await _localStorage.GetItemAsync<string>(ThemeKey);
            _currentTheme = theme ?? "light";
            return _currentTheme;
        }
        catch
        {
            return "light";
        }
    }

    public async Task SetThemeAsync(string theme)
    {
        _currentTheme = theme;
        await _localStorage.SetItemAsync(ThemeKey, theme);
        OnThemeChanged?.Invoke();
    }

    public async Task ToggleThemeAsync()
    {
        var newTheme = _currentTheme == "light" ? "dark" : "light";
        await SetThemeAsync(newTheme);
    }
}
