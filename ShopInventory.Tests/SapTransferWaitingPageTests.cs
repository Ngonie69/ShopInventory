using System.Security.Claims;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Features.SapTransferApprovals.Commands.DecideSapTransferApproval;
using ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApproval;
using ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// /inventory-transfers/sap-approvals as the wash bay uses it: it opens on the transfers waiting for SAP's
/// approval that are coming into WASHBAY, and approves the ones at its own stage from the drawer. Rendered
/// for real, with the page's mediator answering as the API would.
/// </summary>
public sealed class SapTransferWaitingPageTests
{
    private const int Decidable = 89165;
    private const int OtherStage = 89153;

    [Fact]
    public async Task The_wash_bay_opens_on_what_is_waiting_to_come_into_WASHBAY()
    {
        var run = await RenderAsync("WashBay");

        var filter = Assert.Single(run.ListFilters);
        Assert.True(filter.AwaitingApproval);
        Assert.Equal(["WASHBAY"], filter.ToWarehouses);
        Assert.Contains("aria-pressed=\"true\"", Around(run.Html, "Waiting for approval"));
        Assert.Contains("To WASHBAY", run.Html);
        Assert.Contains("Waiting</span>", run.Html);
    }

    [Fact]
    public async Task Everyone_else_opens_on_the_approved_transfers_as_before()
    {
        var run = await RenderAsync("Admin");

        var filter = Assert.Single(run.ListFilters);
        Assert.False(filter.AwaitingApproval);
        Assert.Empty(filter.ToWarehouses);
        Assert.Contains("aria-pressed=\"true\"", Around(run.Html, "Ready to add"));
    }

    [Fact]
    public async Task The_drawer_offers_approve_and_reject_on_a_request_at_the_wash_bays_stage()
    {
        var run = await RenderAsync("WashBay", open: Decidable);

        Assert.Contains("sta-decide-approve", run.Html);
        Assert.Contains("sta-decide-reject", run.Html);
        Assert.Contains("Waiting for your approval at SAP stage &#x27;Factory Washbay&#x27;", run.Html);
        Assert.DoesNotContain("Fix the draft in SAP Business One", run.Html);
    }

    [Fact]
    public async Task The_drawer_says_who_a_request_at_another_stage_waits_on_and_offers_no_decision()
    {
        var run = await RenderAsync("WashBay", open: OtherStage);

        Assert.DoesNotContain("sta-decide-approve", run.Html);
        Assert.Contains("This request waits at SAP stage &#x27;Bulawayo Depot&#x27;, which is not one of yours to decide.", run.Html);
        Assert.DoesNotContain("Waiting for approval in SAP at stage", run.Html);
        Assert.Contains("Decided in SAP Business One by the stage", run.Html);
    }

    [Fact]
    public async Task Approving_sends_the_request_to_SAP_and_keeps_what_SAP_said_on_the_page()
    {
        var run = await RenderAsync("WashBay", open: Decidable, decide: "Approved");

        var decision = Assert.Single(run.Decisions);
        Assert.Equal(98635, decision.Code);
        Assert.Equal("Approved", decision.Decision);
        Assert.DoesNotContain("sta-drawer\"", run.Html);
        Assert.Contains("Approved. The transfer is ready to add", run.Html);
        Assert.Contains("entry 89165", run.Html);
        Assert.Equal(2, run.ListFilters.Count);
    }

    private static string Around(string html, string text)
    {
        var at = html.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{text}' is not on the page");
        var start = html.LastIndexOf("<button", at, StringComparison.Ordinal);
        return html[start..at];
    }

    private sealed record Run(string Html, List<SapTransferQueueFilter> ListFilters, List<DecideSapTransferApprovalCommand> Decisions);

    private static async Task<Run> RenderAsync(string role, int? open = null, string? decide = null)
    {
        var listFilters = new List<SapTransferQueueFilter>();
        var decisions = new List<DecideSapTransferApprovalCommand>();
        var mediator = StubProxy.For<IMediator>((method, args) => method.Name == nameof(IMediator.Send) && args is [var request, ..]
            ? request switch
            {
                GetSapTransferApprovalsQuery list => List(listFilters, list),
                GetSapTransferApprovalQuery detail => Task.FromResult<ErrorOr<SapTransferApprovalDetailDto>>(Detail(detail.DraftEntry)),
                DecideSapTransferApprovalCommand command => Decide(decisions, command),
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
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"));
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

        if (decide is not null)
        {
            await renderer.Dispatcher.InvokeAsync(() => InvokeAsync(page!, "DecideAsync", decide));
        }

        var html = await renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        WriteSnapshot(role, open, decide, html);
        return new Run(html, listFilters, decisions);
    }

    /// <summary>
    /// With <c>SHOPINV_PAGE_SNAPSHOTS</c> set to a folder, each render is written there as HTML, so it can be
    /// opened beside the real stylesheet and looked at.
    /// </summary>
    private static void WriteSnapshot(string role, int? open, string? decide, string html)
    {
        var folder = Environment.GetEnvironmentVariable("SHOPINV_PAGE_SNAPSHOTS");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var name = $"sap-held-{role.ToLowerInvariant()}{(open is int entry ? $"-open-{entry}" : "")}{(decide is null ? "" : $"-{decide.ToLowerInvariant()}")}.html";
        File.WriteAllText(Path.Combine(folder, name), html);
    }

    private static Task<ErrorOr<SapTransferApprovalListResponseDto>> List(List<SapTransferQueueFilter> filters, GetSapTransferApprovalsQuery query)
    {
        var filter = query.Filter ?? new SapTransferQueueFilter();
        filters.Add(filter);
        var items = filter.AwaitingApproval
            ? new List<SapTransferApprovalListItemDto> { Row(Decidable, "PROYG 2", 96285, "Factory Washbay", "Washbay Transfers"), Row(OtherStage, "CMB", 96205, "Bulawayo Depot", "Transfers from CMB") }
            : [];
        return Task.FromResult<ErrorOr<SapTransferApprovalListResponseDto>>(new SapTransferApprovalListResponseDto
        {
            Items = items,
            TotalCount = items.Count,
            TransferCount = items.Count,
            RequestCount = 0,
            Page = 1,
            PageSize = 25
        });
    }

    private static Task<ErrorOr<SapTransferApprovalDecisionResultDto>> Decide(List<DecideSapTransferApprovalCommand> decisions, DecideSapTransferApprovalCommand command)
    {
        decisions.Add(command);
        return Task.FromResult<ErrorOr<SapTransferApprovalDecisionResultDto>>(new SapTransferApprovalDecisionResultDto
        {
            Code = command.Code,
            DraftEntry = Decidable,
            Decision = command.Decision,
            Status = "Approved",
            CanAdd = true,
            Message = "Approved. The transfer is ready to add — find it under Ready to add."
        });
    }

    private static SapTransferApprovalDetailDto Detail(int entry)
    {
        var decidable = entry == Decidable;
        return new SapTransferApprovalDetailDto
        {
            Draft = decidable
                ? Row(Decidable, "PROYG 2", 96285, "Factory Washbay", "Washbay Transfers")
                : Row(OtherStage, "CMB", 96205, "Bulawayo Depot", "Transfers from CMB"),
            Lines =
            [
                new SapTransferDraftLineDto { LineNum = 0, ItemCode = "CRA001", ItemDescription = "Yoghurt crate", Quantity = 120, UoMCode = "EA", FromWarehouse = decidable ? "PROYG 2" : "CMB", ToWarehouse = "WASHBAY", ManagesBatches = false, ManagesSerials = false }
            ],
            Approvals =
            [
                new SapTransferApprovalRequestDto
                {
                    Code = decidable ? 98635 : 98601,
                    Status = "Pending",
                    TemplateCode = decidable ? 91 : 70,
                    TemplateName = decidable ? "Washbay Transfers" : "Transfers from CMB",
                    StageCode = decidable ? 44 : 30,
                    StageName = decidable ? "Factory Washbay" : "Bulawayo Depot",
                    CreatedDate = DateTime.Today,
                    CreatedTime = "08:30:00",
                    Decisions =
                    [
                        new SapTransferApprovalDecisionDto { StageCode = decidable ? 44 : 30, UserCode = "Opsassist", UserName = "Ops assistant", Status = "Pending" }
                    ],
                    CanDecide = decidable,
                    DecideNote = decidable ? null : "This request waits at SAP stage 'Bulawayo Depot', which is not one of yours to decide."
                }
            ]
        };
    }

    private static SapTransferApprovalListItemDto Row(int entry, string from, int draftNum, string stage, string template) => new()
    {
        DraftEntry = entry,
        DraftNum = draftNum,
        Kind = SapHeldDraftKinds.Transfer,
        FromWarehouse = from,
        ToWarehouse = "WASHBAY",
        CreatedDate = DateTime.Today,
        DocDate = DateTime.Today,
        DraftStatus = "Pending",
        RequestCode = entry + 9470,
        RequestCodes = [entry + 9470],
        RequestStatus = "Pending",
        StageName = stage,
        TemplateName = template,
        OriginatorUserCode = "Yogprod",
        OriginatorName = "Yoghurt production",
        CanAdd = false,
        StatusNote = "SAP has this draft as Pending, so it cannot be added."
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
