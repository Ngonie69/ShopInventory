using Blazored.LocalStorage;
using Microsoft.JSInterop;

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
    private readonly IJSRuntime _js;
    private const string ThemeKey = "app-theme";
    private string _currentTheme = "light";

    public event Action? OnThemeChanged;
    public bool IsDarkMode => _currentTheme == "dark";

    public ThemeService(ILocalStorageService localStorage, IJSRuntime js)
    {
        _localStorage = localStorage;
        _js = js;
    }

    public async Task<string> GetThemeAsync()
    {
        try
        {
            var theme = await _localStorage.GetItemAsync<string>(ThemeKey);
            if (theme is not ("light" or "dark"))
            {
                theme = await _js.InvokeAsync<string>("themeManager.getPreferredTheme");
            }

            _currentTheme = theme is "dark" ? "dark" : "light";
            return _currentTheme;
        }
        catch
        {
            _currentTheme = "light";
            return _currentTheme;
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
