using System.Security.Claims;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApproval;
using ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// Two SAP drafts can carry the same draft number. The B1 client tells them apart by the draft entry in
/// its window's title bar, so /inventory-transfers/sap-approvals shows that entry on each row and in the
/// drawer — rendered here for real, with two drafts that share a number.
/// </summary>
public sealed class SapTransferApprovalEntryPageTests
{
    private const int SharedDraftNum = 70412;
    private const int FirstEntry = 61005;
    private const int SecondEntry = 61022;

    [Fact]
    public async Task Rows_that_share_a_draft_number_each_show_their_entry()
    {
        var (html, _) = await RenderAsync(open: null);

        Assert.Equal(2, CountOf(html, $"Draft {SharedDraftNum}"));
        Assert.Contains($"Entry {FirstEntry}", html);
        Assert.Contains($"Entry {SecondEntry}", html);
    }

    [Fact]
    public async Task The_drawer_shows_the_entry_of_the_draft_it_opened()
    {
        var (html, detailReads) = await RenderAsync(open: SecondEntry);

        Assert.Contains("sta-drawer", html);
        Assert.Equal([SecondEntry], detailReads);

        var facts = html[html.IndexOf("sta-d-facts", StringComparison.Ordinal)..];
        Assert.Contains("Draft entry", facts);
        Assert.Contains($">{SecondEntry}<", facts);
        Assert.Contains("In the B1 window's title bar", facts);
    }

    private static int CountOf(string html, string text)
    {
        var count = 0;
        for (var at = html.IndexOf(text, StringComparison.Ordinal); at >= 0; at = html.IndexOf(text, at + text.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<(string Html, List<int> DetailReads)> RenderAsync(int? open)
    {
        var detailReads = new List<int>();
        var mediator = StubProxy.For<IMediator>((method, args) => method.Name == nameof(IMediator.Send) && args is [var request, ..]
            ? request switch
            {
                GetSapTransferApprovalsQuery => Task.FromResult<ErrorOr<SapTransferApprovalListResponseDto>>(new SapTransferApprovalListResponseDto
                {
                    Items = [Draft(SecondEntry, "RETURNS"), Draft(FirstEntry, "WASHBAY")],
                    TotalCount = 2,
                    TransferCount = 2,
                    RequestCount = 0,
                    Page = 1,
                    PageSize = 25
                }),
                GetSapTransferApprovalQuery detail => Detail(detailReads, detail.DraftEntry),
                _ => throw new InvalidOperationException($"{request?.GetType().Name} was not expected.")
            }
            : throw new InvalidOperationException($"IMediator.{method.Name} was not expected."));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddSingleton(mediator);
        services.AddSingleton<ISnackbar>(new CreditNoteApprovalAddPageTests.SilentSnackbar());
        services.AddSingleton(StubProxy.For<IMasterDataCacheService>((method, _) => method.Name == nameof(IMasterDataCacheService.GetWarehousesAsync)
            ? Task.FromResult(new List<WarehouseDto>())
            : throw new InvalidOperationException($"IMasterDataCacheService.{method.Name} was not expected.")));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new CreditNoteApprovalAddPageTests.InteractivePageRenderer(provider);

        SapTransferApprovals? page = null;
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "Test"));
        RenderFragment content = builder =>
        {
            builder.OpenComponent<SapTransferApprovals>(0);
            builder.AddComponentReferenceCapture(1, instance => page = (SapTransferApprovals)instance);
            builder.CloseComponent();
        };

        HtmlRootComponent output = await renderer.Dispatcher.InvokeAsync(() =>
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

        if (open is int entry)
        {
            await renderer.Dispatcher.InvokeAsync(() => InvokeAsync(page!, "OpenAsync", entry));
        }

        return (await renderer.Dispatcher.InvokeAsync(output.ToHtmlString), detailReads);
    }

    private static Task<ErrorOr<SapTransferApprovalDetailDto>> Detail(List<int> reads, int entry)
    {
        reads.Add(entry);
        return Task.FromResult<ErrorOr<SapTransferApprovalDetailDto>>(new SapTransferApprovalDetailDto
        {
            Draft = Draft(entry, entry == SecondEntry ? "RETURNS" : "WASHBAY")
        });
    }

    private static SapTransferApprovalListItemDto Draft(int entry, string to) => new()
    {
        DraftEntry = entry,
        DraftNum = SharedDraftNum,
        Kind = SapHeldDraftKinds.Transfer,
        FromWarehouse = "PROYG 2",
        ToWarehouse = to,
        CreatedDate = DateTime.Today,
        DocDate = DateTime.Today,
        DraftStatus = "Open",
        RequestCode = entry + 9000,
        RequestCodes = [entry + 9000],
        CanAdd = true
    };

    /// <summary>Runs a handler the way a click does: await it, then re-render.</summary>
    private static async Task InvokeAsync(SapTransferApprovals page, string name, params object[] args)
    {
        var method = typeof(SapTransferApprovals).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"SapTransferApprovals.{name} no longer exists.");
        await (Task)method.Invoke(page, args)!;

        typeof(ComponentBase)
            .GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(page, null);
    }
}
