using System.Reflection;
using ClosedXML.Excel;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;
using InteractivePageRenderer = ShopInventory.Tests.CreditNoteApprovalAddPageTests.InteractivePageRenderer;

namespace ShopInventory.Tests;

/// <summary>
/// /shop-sales's Export to Excel, rendered for real: it pages through every sale of the period shown, not the
/// 25 the list holds, and puts them on the workbook's Sales sheet after the analysis.
/// </summary>
public sealed class ShopSalesExportPageTests
{
    private const int SalesInPeriod = 1_150; // three pages of 500

    [Fact]
    public async Task Export_lists_every_sale_of_the_period_after_the_analysis()
    {
        var reads = new List<DesktopSalesQuery>();
        await using var page = await SalesPage.OpenAsync(query =>
        {
            reads.Add(query);
            var first = (query.Page - 1) * query.PageSize;
            var count = Math.Max(0, Math.Min(query.PageSize, SalesInPeriod - first));
            return new DesktopSalesListResponse
            {
                Sales = Enumerable.Range(first, count).Select(Sale).ToList(),
                TotalCount = SalesInPeriod,
                Page = query.Page,
                PageSize = query.PageSize,
                HasMore = first + count < SalesInPeriod
            };
        });
        var listReads = reads.Count;

        await page.ExportAsync();

        var exportReads = reads.Skip(listReads).ToList();
        Assert.Equal([1, 2, 3], exportReads.Select(query => query.Page));
        Assert.All(exportReads, query => Assert.Null(query.Search));

        var (fileName, base64) = Assert.Single(page.Downloads);
        Assert.Equal("Sales_CIS006_20261116_20261121.xlsx", fileName);
        using var workbook = new XLWorkbook(new MemoryStream(Convert.FromBase64String(base64)));
        Assert.Equal("Sales", workbook.Worksheets.Last().Name);
        var numbers = workbook.Worksheet("Sales").Column(2).CellsUsed()
            .Select(cell => cell.GetString())
            .Where(text => text.StartsWith("KS-", StringComparison.Ordinal))
            .ToHashSet();
        Assert.Equal(SalesInPeriod, numbers.Count);
    }

    [Fact]
    public async Task A_page_that_cannot_be_read_downloads_nothing_and_says_so()
    {
        await using var page = await SalesPage.OpenAsync(query => query.Page == 2
            ? null
            : new DesktopSalesListResponse { Sales = [Sale(0)], TotalCount = 2, Page = query.Page, PageSize = query.PageSize, HasMore = true });

        await page.ExportAsync();

        Assert.Empty(page.Downloads);
        Assert.Contains("The sales could not be listed for the workbook.", await page.HtmlAsync());
    }

    private static DesktopSaleDto Sale(int index) => new()
    {
        ExternalReferenceId = $"KS-{index:00000}",
        CardCode = "CIS006",
        WarehouseCode = "KEFSHOP",
        DocDate = new DateTime(2026, 11, 16).AddDays(index % 6),
        CreatedAt = new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc),
        Currency = "USD",
        TotalAmount = 10m,
        AmountPaid = 10m,
        ConsolidationStatus = "Consolidated"
    };

    private static DesktopSalesAnalysisResult Report() => new()
    {
        FromDate = new DateTime(2026, 11, 16),
        ToDate = new DateTime(2026, 11, 21),
        CardCode = "CIS006",
        CreditsCounted = true,
        PaymentMethods = ["Cash"],
        Currencies =
        [
            new DesktopSalesCurrencyAnalysis
            {
                Currency = "USD",
                SalesCount = SalesInPeriod,
                TotalAmount = SalesInPeriod * 10m,
                ByBusinessPartner = [new() { Key = "CIS006", Label = "Counter Sales", SalesCount = SalesInPeriod, TotalAmount = SalesInPeriod * 10m }],
            },
        ],
    };

    private sealed class SalesPage : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly InteractivePageRenderer renderer;
        private readonly HtmlRootComponent output;
        private readonly ShopSales page;

        public List<(string FileName, string Base64)> Downloads { get; }

        private SalesPage(ServiceProvider provider, InteractivePageRenderer renderer, HtmlRootComponent output, ShopSales page, List<(string, string)> downloads)
        {
            this.provider = provider;
            this.renderer = renderer;
            this.output = output;
            this.page = page;
            Downloads = downloads;
        }

        public static async Task<SalesPage> OpenAsync(Func<DesktopSalesQuery, DesktopSalesListResponse?> list)
        {
            var downloads = new List<(string, string)>();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthorizationCore();
            services.AddScoped<PageLifetime>();
            services.AddSingleton<IReportExportService>(new ReportExportService());
            services.AddSingleton(StubProxy.For<IJSRuntime>((method, args) =>
            {
                if (args?[0] is not "downloadFile" || args[^1] is not object?[] { Length: 2 } call)
                    throw new InvalidOperationException($"IJSRuntime.{method.Name}({args?[0]}) was not expected.");
                downloads.Add(((string)call[0]!, (string)call[1]!));
                return Activator.CreateInstance(method.ReturnType);
            }));
            services.AddSingleton(StubProxy.For<IMediator>((method, args) =>
                args?[0] is GetDesktopSalesAnalysisQuery
                    ? Task.FromResult<ErrorOr<DesktopSalesAnalysisResult>>(Report())
                    : throw new InvalidOperationException($"IMediator.{method.Name}({args?[0]}) was not expected.")));
            services.AddSingleton(StubProxy.For<IDesktopIntegrationService>((method, args) => method.Name switch
            {
                nameof(IDesktopIntegrationService.ReadDesktopSalesAsync) =>
                    Task.FromResult<(DesktopSalesListResponse?, string?)>((list((DesktopSalesQuery)args![0]!), "refused")),
                nameof(IDesktopIntegrationService.GetDesktopSalesAsync) =>
                    Task.FromResult<DesktopSalesListResponse?>(new DesktopSalesListResponse()),
                _ => throw new InvalidOperationException($"IDesktopIntegrationService.{method.Name} was not expected.")
            }));

            var provider = services.BuildServiceProvider();
            var renderer = new InteractivePageRenderer(provider);

            ShopSales? page = null;
            RenderFragment content = builder =>
            {
                builder.OpenComponent<ShopSales>(0);
                builder.AddComponentReferenceCapture(1, instance => page = (ShopSales)instance);
                builder.CloseComponent();
            };

            var output = await renderer.Dispatcher.InvokeAsync(() =>
                renderer.BeginRenderingComponent(
                    typeof(CascadingValue<int>),
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(CascadingValue<int>.Value)] = 0,
                        [nameof(CascadingValue<int>.ChildContent)] = content
                    })));
            await output.QuiescenceTask;

            Assert.NotNull(page);
            return new SalesPage(provider, renderer, output, page!, downloads);
        }

        public Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(output.ToHtmlString);

        public Task ExportAsync() => renderer.Dispatcher.InvokeAsync(() =>
            (Task)typeof(ShopSales).GetMethod("ExportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!);

        public async ValueTask DisposeAsync()
        {
            // Through the interface: Renderer's own member trips BL0006.
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await provider.DisposeAsync();
        }
    }
}
