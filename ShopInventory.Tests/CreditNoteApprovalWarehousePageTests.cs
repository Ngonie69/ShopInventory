using System.Security.Claims;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApproval;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalOriginators;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;
using static ShopInventory.Tests.CreditNoteApprovalAddPageTests;
using ReturnWarehouse = ShopInventory.Web.Features.CreditNoteApprovals.CreditNoteReturnWarehouse;

namespace ShopInventory.Tests;

/// <summary>
/// The warehouse a credit memo draft credits its stock into, as /credit-notes/approvals shows it. Returns
/// go to WASHBAY; a draft keyed against any other warehouse — one in production credited
/// crates into CM-BAY 4 — is called out before the approver approves or adds it.
/// </summary>
public sealed class CreditNoteApprovalWarehousePageTests
{
    private const int Code = 86310;

    [Fact]
    public void Only_a_named_warehouse_other_than_the_wash_bays_is_unusual()
    {
        Assert.False(ReturnWarehouse.IsUnusual("WASHBAY"));
        Assert.False(ReturnWarehouse.IsUnusual(" washbay "));
        Assert.False(ReturnWarehouse.IsUnusual(null));
        Assert.False(ReturnWarehouse.IsUnusual(""));
        Assert.True(ReturnWarehouse.IsUnusual("CM-BAY 4"));

        var lines = new[] { Line(0, "CM-BAY 4"), Line(1, "WASHBAY"), Line(2, "cm-bay 4"), Line(3, null) };
        Assert.Equal(["CM-BAY 4", "WASHBAY"], ReturnWarehouse.Used(lines));
        Assert.Equal(["CM-BAY 4"], ReturnWarehouse.Unusual(lines));
    }

    [Fact]
    public async Task A_draft_crediting_another_warehouse_is_called_out_before_approving()
    {
        var drawer = await RenderDrawerAsync(Item(canApprove: true), [Line(0, "CM-BAY 4"), Line(1, "CM-BAY 4")]);

        Assert.Contains("Unusual warehouse: <span class=\"cna-whs-code\">CM-BAY 4</span>", drawer);
        Assert.Contains("Credit notes normally return stock to WASHBAY.", drawer);
        Assert.Contains("Check it is right before approving.", drawer);
        Assert.Contains("cna-whs is-unusual", drawer);
        Assert.Contains("cna-line-whs is-unusual", drawer);
        Assert.Contains("cna-whs-foot", drawer);
        Assert.Contains("cna-decide-approve", drawer);
    }

    [Fact]
    public async Task A_wash_bay_draft_shows_its_warehouse_and_no_warning()
    {
        var drawer = await RenderDrawerAsync(Item(canApprove: true), [Line(0, "WASHBAY"), Line(1, "WASHBAY")]);

        Assert.Contains("Credit warehouse", drawer);
        Assert.Contains(">WASHBAY</div>", drawer);
        Assert.Contains("<th>Whse</th>", drawer);
        Assert.DoesNotContain("Unusual warehouse", drawer);
        Assert.DoesNotContain("is-unusual", drawer);
        Assert.DoesNotContain("cna-whs-foot", drawer);
    }

    [Fact]
    public async Task Lines_split_across_warehouses_name_each_and_warn_of_the_odd_one()
    {
        var drawer = await RenderDrawerAsync(Item(canApprove: true), [Line(0, "WASHBAY"), Line(1, "CM-BAY 4")]);

        Assert.Contains("WASHBAY and CM-BAY 4", drawer);
        Assert.Contains("Unusual warehouse: <span class=\"cna-whs-code\">CM-BAY 4</span>", drawer);
    }

    private static CreditNoteDraftLineDto Line(int lineNum, string? warehouse) => new()
    {
        LineNum = lineNum,
        ItemCode = lineNum == 0 ? "CRA002" : "CRA001",
        ItemDescription = lineNum == 0 ? "Crates Black Yoghurt" : "Crates White Yoghurt",
        Quantity = 10m,
        UnitPrice = 10m,
        LineTotal = 100m,
        WarehouseCode = warehouse
    };

    private static CreditNoteApprovalListItemDto Item(bool canApprove) => new()
    {
        Code = Code,
        Status = "Pending",
        StageName = "Factory Washbay",
        DraftEntry = 81600,
        DraftNum = 63014,
        CardCode = "C00091",
        CardName = "Corner Store",
        NumAtCard = "800500",
        DocDate = new DateTime(2026, 11, 11),
        DocTotal = 554.40m,
        DocCurrency = "USD",
        DraftAuthorizationStatus = "Pending",
        DraftIsOpen = true,
        CanDecide = true,
        CanApprove = canApprove,
        HasAttachment = false
    };

    /// <summary>Renders the page with one request, opens it, and returns the drawer's HTML.</summary>
    private static async Task<string> RenderDrawerAsync(CreditNoteApprovalListItemDto item, List<CreditNoteDraftLineDto> lines)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddSingleton<IMediator>(Mediator(item, lines));
        services.AddSingleton<ISnackbar>(new SilentSnackbar());
        services.AddSingleton(StubProxy.Unused<IAuditService>());
        services.AddSingleton(StubProxy.Unused<IJSRuntime>());

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new InteractivePageRenderer(provider);

        CreditNoteApprovals? page = null;
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "Test"));
        RenderFragment content = builder =>
        {
            builder.OpenComponent<CreditNoteApprovals>(0);
            builder.AddComponentReferenceCapture(1, instance => page = (CreditNoteApprovals)instance);
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

        await renderer.Dispatcher.InvokeAsync(() => Invoke(page!, "OpenAsync", Code));
        var drawer = await renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.Contains("cna-drawer", drawer);
        return drawer;
    }

    private static IMediator Mediator(CreditNoteApprovalListItemDto item, List<CreditNoteDraftLineDto> lines) =>
        StubProxy.For<IMediator>((method, args) => method.Name == nameof(IMediator.Send) && args is [var request, ..]
            ? request switch
            {
                GetCreditNoteApprovalsQuery => Task.FromResult<ErrorOr<CreditNoteApprovalListResponseDto>>(new CreditNoteApprovalListResponseDto
                {
                    Items = [item],
                    TotalCount = 1,
                    Page = 1,
                    PageSize = 25,
                    Status = "open"
                }),
                GetCreditNoteApprovalCountsQuery => Task.FromResult<ErrorOr<CreditNoteApprovalCountsDto>>(new CreditNoteApprovalCountsDto { Pending = 1 }),
                GetCreditNoteApprovalOriginatorsQuery => Task.FromResult<ErrorOr<List<CreditNoteApprovalOriginatorDto>>>(new List<CreditNoteApprovalOriginatorDto>()),
                GetCreditNoteApprovalQuery => Task.FromResult<ErrorOr<CreditNoteApprovalDetailDto>>(new CreditNoteApprovalDetailDto { Request = item, Lines = lines }),
                _ => throw new InvalidOperationException($"{request?.GetType().Name} was not expected.")
            }
            : throw new InvalidOperationException($"IMediator.{method.Name} was not expected."));
}
