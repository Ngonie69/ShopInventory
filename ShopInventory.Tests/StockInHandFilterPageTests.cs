using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;
using InteractivePageRenderer = ShopInventory.Tests.CreditNoteApprovalAddPageTests.InteractivePageRenderer;

namespace ShopInventory.Tests;

/// <summary>
/// The item chips on /stock-in-hand, rendered for real against a stock cache holding four items
/// that each sit in a different place: free to sell, fully committed, oversold, and none on hand.
/// </summary>
/// <remarks>
/// "Available" is free to sell above zero, which is not the same as "On the shelf" (anything on
/// hand): stock already promised to orders is on the shelf but cannot be sold again.
/// </remarks>
public sealed class StockInHandFilterPageTests
{
    private static readonly ProductDto[] Shelf =
    [
        new() { ItemCode = "CHE001", ItemName = "Cheddar 1kg", UoM = "Each", QuantityInStock = 10m, QuantityCommitted = 0m },
        new() { ItemCode = "CHE002", ItemName = "Gouda 1kg", UoM = "Each", QuantityInStock = 5m, QuantityCommitted = 5m },
        new() { ItemCode = "BUT025", ItemName = "Butter Salted", UoM = "Kilogram", QuantityInStock = 3m, QuantityCommitted = 8m },
        new() { ItemCode = "BUC003", ItemName = "Printed Bucket", UoM = "Each", QuantityInStock = 0m, QuantityCommitted = 0m }
    ];

    [Fact]
    public async Task Available_counts_and_shows_only_items_free_to_sell()
    {
        await using var page = await StockPage.OpenAsync();

        var all = await page.HtmlAsync();
        Assert.Equal(1, ChipCount(all, "Available"));
        Assert.Equal(3, ChipCount(all, "On the shelf"));
        Assert.Equal(1, ChipCount(all, "At zero"));
        Assert.Equal(1, ChipCount(all, "Oversold"));
        Assert.Equal(["Butter Salted", "Cheddar 1kg", "Gouda 1kg", "Printed Bucket"], ItemNames(all));

        await page.ChooseAsync("available");
        var available = await page.HtmlAsync();

        Assert.Equal("Available", LitChip(available));
        // Gouda is on the shelf but all of it is committed; the butter is oversold.
        Assert.Equal(["Cheddar 1kg"], ItemNames(available));
        Assert.Contains("1 of 4", available);
    }

    [Fact]
    public async Task On_the_shelf_still_includes_committed_stock()
    {
        await using var page = await StockPage.OpenAsync();

        await page.ChooseAsync("stock");
        var shelf = await page.HtmlAsync();

        Assert.Equal("On the shelf", LitChip(shelf));
        Assert.Equal(["Butter Salted", "Cheddar 1kg", "Gouda 1kg"], ItemNames(shelf));
    }

    /// <summary>
    /// A past moment offers no Available chip (committed is not kept historically). Picking one while
    /// Available is on must not leave the list filtered with no chip lit.
    /// </summary>
    [Fact]
    public async Task A_chip_the_moment_does_not_offer_falls_back_to_all()
    {
        await using var page = await StockPage.OpenAsync();
        await page.ChooseAsync("available");

        await page.SetModeAsync("Opening");
        var past = await page.HtmlAsync();

        Assert.DoesNotContain(">Available <", past);
        Assert.Equal("All", LitChip(past));
        Assert.Equal(4, ItemNames(past).Count);
    }

    private static int ChipCount(string html, string label)
    {
        var match = Regex.Match(html, $@"<button[^>]*class=""sih-chip[^""]*""[^>]*>\s*{Regex.Escape(label)} <span class=""sih-chip-n"">(\d+)</span>");
        Assert.True(match.Success, $"No \"{label}\" chip was rendered.");
        return int.Parse(match.Groups[1].Value);
    }

    private static string LitChip(string html)
    {
        var lit = Regex.Matches(html, @"<button[^>]*class=""sih-chip is-on""[^>]*>\s*([^<]+?) <span")
            .Select(match => match.Groups[1].Value)
            .ToList();
        return Assert.Single(lit);
    }

    private static List<string> ItemNames(string html) =>
        Regex.Matches(html, @"<span class=""sih-item-name"">([^<]+)</span>")
            .Select(match => match.Groups[1].Value)
            .ToList();

    private sealed class StockPage : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly InteractivePageRenderer renderer;
        private readonly HtmlRootComponent output;
        private readonly StockInHand page;

        private StockPage(ServiceProvider provider, InteractivePageRenderer renderer, HtmlRootComponent output, StockInHand page)
        {
            this.provider = provider;
            this.renderer = renderer;
            this.output = output;
            this.page = page;
        }

        public static async Task<StockPage> OpenAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthorizationCore();
            services.AddScoped<PageLifetime>();
            services.AddSingleton<NavigationManager>(new FixedNavigationManager());
            services.AddSingleton(StubProxy.Unused<IJSRuntime>());
            services.AddSingleton(StubProxy.Unused<IStockHistoryService>());
            services.AddSingleton(StubProxy.For<IMasterDataCacheService>((method, _) =>
                method.Name == nameof(IMasterDataCacheService.GetWarehousesAsync)
                    ? Task.FromResult(new List<WarehouseDto>())
                    : throw new InvalidOperationException($"IMasterDataCacheService.{method.Name} was not expected.")));
            services.AddSingleton(StubProxy.For<IWarehouseStockCacheService>((method, _) =>
                method.Name == nameof(IWarehouseStockCacheService.GetAllCachedStockAsync)
                    ? Task.FromResult<WarehouseProductsResponse?>(new WarehouseProductsResponse { WarehouseCode = "KEFSHOP", Products = [.. Shelf] })
                    : throw new InvalidOperationException($"IWarehouseStockCacheService.{method.Name} was not expected.")));

            var provider = services.BuildServiceProvider();
            var renderer = new InteractivePageRenderer(provider);

            StockInHand? page = null;
            var user = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, "ShopStock"), new Claim("warehouse", "KEFSHOP")], "Test"));
            RenderFragment content = builder =>
            {
                builder.OpenComponent<StockInHand>(0);
                builder.AddComponentReferenceCapture(1, instance => page = (StockInHand)instance);
                builder.CloseComponent();
            };

            var output = await renderer.Dispatcher.InvokeAsync(() =>
                renderer.BeginRenderingComponent(
                    typeof(CascadingValue<Task<AuthenticationState>>),
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(CascadingValue<Task<AuthenticationState>>.Value)] = Task.FromResult(new AuthenticationState(user)),
                        [nameof(CascadingValue<Task<AuthenticationState>>.IsFixed)] = true,
                        [nameof(CascadingValue<Task<AuthenticationState>>.ChildContent)] = content
                    })));
            await output.QuiescenceTask;

            Assert.NotNull(page);
            return new StockPage(provider, renderer, output, page!);
        }

        public Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(output.ToHtmlString);

        /// <summary>What a chip's click does: <c>view = filter.Key</c>, then a re-render.</summary>
        public Task ChooseAsync(string key) => SetFieldAsync("view", key);

        /// <summary>The moment changing without a reload, so only the chips' reaction is under test.</summary>
        public Task SetModeAsync(string moment)
        {
            var field = Field("mode");
            return SetFieldAsync("mode", Enum.Parse(field.FieldType, moment));
        }

        private Task SetFieldAsync(string name, object value) => renderer.Dispatcher.InvokeAsync(() =>
        {
            Field(name).SetValue(page, value);
            typeof(ComponentBase)
                .GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, null);
        });

        private static FieldInfo Field(string name) =>
            typeof(StockInHand).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"StockInHand.{name} no longer exists.");

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class FixedNavigationManager : NavigationManager
    {
        public FixedNavigationManager() => Initialize("https://shop.test/", "https://shop.test/stock-in-hand");

        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}
