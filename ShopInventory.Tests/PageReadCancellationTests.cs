using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using ShopInventory.Web.Components;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Leaving a page cancels the reads it bound to its life, and nothing else: not a save, not a
/// background sweep, not work shared with other users, not a page that never opted in.
/// </summary>
public sealed class PageReadCancellationTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_bound_read_is_cancelled_when_its_page_is_left()
    {
        var lifetime = new PageLifetime();
        using var client = CreateClient(new HangingHandler());

        Task<HttpResponseMessage> read;
        using (PageReads.Bind(lifetime))
        {
            read = client.GetAsync("api/invoices");
        }

        await Task.Delay(100);
        Assert.False(read.IsCompleted);

        lifetime.Renew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Soon));
    }

    [Fact]
    public async Task A_save_is_never_cut_off_by_leaving_the_page()
    {
        var lifetime = new PageLifetime();
        var inner = new HangingHandler();
        using var client = CreateClient(inner);

        Task<HttpResponseMessage> save;
        using (PageReads.Bind(lifetime))
        {
            save = client.PostAsync("api/invoices", new StringContent("{}"));
        }

        await Task.Delay(100);
        lifetime.Renew();
        await Task.Delay(100);
        Assert.False(save.IsCompleted);

        inner.Release();
        Assert.Equal(HttpStatusCode.OK, (await save.WaitAsync(Soon)).StatusCode);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("background")]
    [InlineData("detached")]
    public async Task A_read_that_is_not_the_pages_own_outlives_the_page(string kind)
    {
        var lifetime = new PageLifetime();
        var inner = new HangingHandler();
        using var client = CreateClient(inner);

        Task<HttpResponseMessage> read;
        using (kind == "unbound" ? null : PageReads.Bind(lifetime))
        using (kind == "background" ? SapBackgroundPriority.Begin() : null)
        using (kind == "detached" ? PageReads.Detach() : null)
        {
            read = client.GetAsync("api/figures");
        }

        await Task.Delay(100);
        lifetime.Renew();
        await Task.Delay(100);
        Assert.False(read.IsCompleted);

        inner.Release();
        Assert.Equal(HttpStatusCode.OK, (await read.WaitAsync(Soon)).StatusCode);
    }

    [Fact]
    public void A_flow_knows_its_page_has_been_left_and_a_new_binding_does_not()
    {
        var lifetime = new PageLifetime();
        Assert.False(PageReads.HasLeft);

        using (PageReads.Bind(lifetime))
        {
            Assert.False(PageReads.HasLeft);
            lifetime.Renew();
            Assert.True(PageReads.HasLeft);

            using (PageReads.Bind(lifetime))
            {
                Assert.False(PageReads.HasLeft);
            }

            using (PageReads.Detach())
            {
                Assert.False(PageReads.HasLeft);
            }

            Assert.True(PageReads.HasLeft);
        }

        Assert.False(PageReads.HasLeft);
    }

    [Fact]
    public void A_toast_from_a_page_that_has_been_left_is_dropped_and_every_other_toast_shown()
    {
        var shown = new RecordingSnackbar();
        var snackbar = new PageAwareSnackbar(shown);
        var lifetime = new PageLifetime();

        snackbar.Add("Saved", Severity.Success);
        using (PageReads.Bind(lifetime))
        {
            snackbar.Add("Loaded from cache", Severity.Info);
            lifetime.Renew();
            snackbar.Add("Failed to load invoices", Severity.Error);
            snackbar.Add(new MarkupString("<b>Failed</b>"), Severity.Error);
        }

        using (PageReads.Bind(lifetime))
        {
            snackbar.Add("Welcome to the next page", Severity.Info);
        }

        Assert.Equal(["Saved", "Loaded from cache", "Welcome to the next page"], shown.Messages);
    }

    [Fact]
    public async Task The_boundary_ends_a_pages_life_only_when_the_page_itself_changes()
    {
        var lifetime = new PageLifetime();
        var services = new ServiceCollection();
        services.AddSingleton(lifetime);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<BoundaryHost>());
        var first = lifetime.Token;

        // The same page again: /  and /dashboard are both Home, and Blazor keeps the instance.
        await renderer.Dispatcher.InvokeAsync(() => BoundaryHost.Current!.Show(typeof(HomeStandIn)));
        Assert.False(first.IsCancellationRequested);

        await renderer.Dispatcher.InvokeAsync(() => BoundaryHost.Current!.Show(typeof(InvoicesStandIn)));
        Assert.True(first.IsCancellationRequested);
        Assert.False(lifetime.Token.IsCancellationRequested);
    }

    private static HttpClient CreateClient(HttpMessageHandler inner) =>
        new(new PageReadCancellationHandler { InnerHandler = inner })
        {
            BaseAddress = new Uri("https://api.invalid/")
        };

    /// <summary>Holds every request until released, or until the request is cancelled.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class HomeStandIn;

    private sealed class InvoicesStandIn;

    private sealed class BoundaryHost : ComponentBase
    {
        public static BoundaryHost? Current;

        private Type _page = typeof(HomeStandIn);

        public void Show(Type page)
        {
            _page = page;
            StateHasChanged();
        }

        protected override void OnInitialized() => Current = this;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<PageLifetimeBoundary>(0);
            builder.AddComponentParameter(1, nameof(PageLifetimeBoundary.PageType), _page);
            builder.CloseComponent();
        }
    }

    private sealed class RecordingSnackbar : ISnackbar
    {
        public List<string> Messages { get; } = [];

        public IEnumerable<Snackbar> ShownSnackbars => [];

        public SnackbarConfiguration Configuration { get; } = new();

        public event Action? OnSnackbarsUpdated { add { } remove { } }

        public Snackbar? Add(string message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null)
        {
            Messages.Add(message);
            return null;
        }

        public Snackbar? Add(MarkupString message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null)
        {
            Messages.Add(message.Value);
            return null;
        }

        public Snackbar? Add(RenderFragment message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null)
        {
            Messages.Add("(fragment)");
            return null;
        }

        public Snackbar? Add<T>(Dictionary<string, object>? componentParameters = null, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null)
            where T : IComponent
        {
            Messages.Add(typeof(T).Name);
            return null;
        }

        public void Clear() { }

        public void Remove(Snackbar snackbar) { }

        public void RemoveByKey(string key) { }

        public void Dispose() { }
    }
}
