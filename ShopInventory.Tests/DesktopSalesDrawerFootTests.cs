using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// What the /desktop-sales drawer footer says about a sale whose fiscalisation failed, rendered for real.
/// </summary>
/// <remarks>
/// The footer used to show a refusal only where no post button stood, and only the post refusal. An
/// Admin always has "Post without receipt" on a failed till sale, so a sale the API would not let anyone
/// retry — marked for reconciliation under the platform — showed that one button and no word on why
/// Retry was missing.
/// </remarks>
public sealed class DesktopSalesDrawerFootTests
{
    private const string Reconcile =
        "The platform could not say whether this receipt was signed. Look it up on the fiscalisation console before retrying.";

    private const string NotFiscalised = "This sale has no fiscal receipt yet, so it cannot be posted to SAP.";

    [Fact]
    public async Task A_refused_retry_says_why_beside_post_without_receipt()
    {
        var foot = await FootAsync("Admin", Sale(fiscal: "Failed", fiscaliseRefusal: Reconcile, postRefusal: NotFiscalised, withoutReceipt: true));

        Assert.Contains("Post without receipt", foot);
        Assert.Contains(Reconcile, foot);
        Assert.DoesNotContain("Retry fiscalisation", foot);
        Assert.DoesNotContain(NotFiscalised, foot);
    }

    [Fact]
    public async Task A_refused_retry_says_why_to_a_cashier_too()
    {
        var foot = await FootAsync("Cashier", Sale(fiscal: "Failed", fiscaliseRefusal: Reconcile, postRefusal: NotFiscalised, withoutReceipt: true));

        Assert.DoesNotContain("Post without receipt", foot);
        Assert.DoesNotContain("Retry fiscalisation", foot);
        Assert.Contains(Reconcile, foot);
    }

    [Fact]
    public async Task A_retryable_sale_shows_retry_and_no_reason()
    {
        var foot = await FootAsync("Admin", Sale(fiscal: "Failed", fiscaliseRefusal: null, postRefusal: NotFiscalised, withoutReceipt: true));

        Assert.Contains("Retry fiscalisation", foot);
        Assert.Contains("Post without receipt", foot);
        Assert.DoesNotContain("ops-ds-foot-why", foot);
    }

    [Fact]
    public async Task A_signed_sale_that_cannot_post_still_says_why()
    {
        const string held = "Held for the end-of-day consolidation.";
        var foot = await FootAsync("Cashier", Sale(fiscal: "Success", fiscaliseRefusal: "This sale is already fiscalised.", postRefusal: held, withoutReceipt: false));

        Assert.Contains(held, foot);
        Assert.DoesNotContain("already fiscalised", foot);
        Assert.DoesNotContain("Post to SAP", foot);
    }

    private static DesktopSaleDto Sale(string fiscal, string? fiscaliseRefusal, string? postRefusal, bool withoutReceipt) => new()
    {
        Id = 1,
        SaleNumber = "INV10001",
        ExternalReferenceId = "KEF-TST-20261109-0000000000AA",
        SourceSystem = "KefalosShopTill",
        CardCode = "C00001",
        DocDate = new DateTime(2026, 11, 9),
        TotalAmount = 6.47m,
        VatAmount = 0.87m,
        Currency = "USD",
        FiscalizationStatus = fiscal,
        FiscalizationAttempts = 1,
        ConsolidationStatus = "Pending",
        WarehouseCode = "KEFSHOP",
        PaymentMethod = "Cash",
        AmountPaid = 10m,
        CreatedAt = new DateTime(2026, 11, 9, 12, 15, 0, DateTimeKind.Utc),
        FiscaliseRefusal = fiscaliseRefusal,
        PostRefusal = postRefusal,
        CanPostWithoutReceipt = withoutReceipt
    };

    /// <summary>Renders the page with one sale, opens its drawer and returns the drawer's footer.</summary>
    private static async Task<string> FootAsync(string role, DesktopSaleDto sale)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddSingleton(StubProxy.For<IDesktopIntegrationService>((method, _) => method.Name switch
        {
            nameof(IDesktopIntegrationService.ReadDesktopSalesAsync) =>
                Task.FromResult<(DesktopSalesListResponse?, string?)>((
                    new DesktopSalesListResponse { Sales = [sale], TotalCount = 1, Page = 1, PageSize = 50 }, null)),
            nameof(IDesktopIntegrationService.GetEndOfDayReportAsync) =>
                Task.FromResult<(EndOfDayReportDto?, string?)>((null, null)),
            _ => throw new InvalidOperationException($"IDesktopIntegrationService.{method.Name} was not expected.")
        }));
        // The page subscribes to two pushes, unsubscribes on dispose and shows whether the hub is live.
        services.AddSingleton(StubProxy.For<INotificationHubService>((method, _) =>
            method.Name == "get_IsConnected" ? true
            : method.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal)
                ? null
                : throw new InvalidOperationException($"INotificationHubService.{method.Name} was not expected.")));
        services.AddSingleton(StubProxy.For<IAuditService>((_, _) => Task.CompletedTask));
        services.AddSingleton(StubProxy.Unused<IReportExportService>());
        services.AddSingleton(StubProxy.Unused<ICreditNoteService>());
        services.AddSingleton(StubProxy.Unused<IJSRuntime>());
        services.AddSingleton<ISnackbar>(new CreditNoteApprovalAddPageTests.SilentSnackbar());

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new CreditNoteApprovalAddPageTests.InteractivePageRenderer(provider);

        DesktopSales? page = null;
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"));
        RenderFragment content = builder =>
        {
            builder.OpenComponent<DesktopSales>(0);
            builder.AddComponentReferenceCapture(1, instance => page = (DesktopSales)instance);
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

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var select = typeof(DesktopSales).GetMethod("SelectSale", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("DesktopSales.SelectSale no longer exists.");
            await (Task)select.Invoke(page, [sale])!;
            typeof(ComponentBase)
                .GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, null);
        });

        var html = await renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        var start = html.IndexOf("class=\"ops-drawer-foot\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "The drawer did not open.");
        var end = html.IndexOf("</aside>", start, StringComparison.Ordinal);
        return html[start..end];
    }
}
