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

namespace ShopInventory.Tests;

/// <summary>
/// What /credit-notes/approvals shows for a held draft whose credit was keyed straight into SAP while it
/// waited — a production request whose credit was keyed again the same afternoon. SAP still
/// says Pending, so without the flag the row looks like any other and the drawer offers Approve.
/// </summary>
public sealed class CreditNoteApprovalAlreadyCreditedPageTests
{
    private const int Code = 84190;
    private const string Customer = "Corner Store";
    /// <summary>The API's sentence, which ends on what the reader can do about it.</summary>
    private static string Note(bool canReject) =>
        "Already credited: credit note 61455 was entered by AR1 on 15 Aug 2026 for the same customer, reference and total. "
        + "Approving or adding this draft would credit the customer twice. "
        + (canReject ? "Reject it, or remove the draft in SAP." : "Remove the draft in SAP.");

    [Fact]
    public async Task The_row_and_the_drawer_say_already_credited_and_offer_reject_but_not_approve()
    {
        var (list, drawer) = await RenderAsync(Item(canDecide: true));

        Assert.Contains("cna-pill is-credited", list);
        Assert.Contains("Already credited", list);
        Assert.Contains("CN 61455", list);

        Assert.Contains("Already credited as credit note 61455", drawer);
        Assert.Contains("Entered by AR1 on 15 Aug 2026 for the same customer, reference and total.", drawer);
        Assert.Contains("Reject it, or remove the draft in SAP.", drawer);
        Assert.Contains("cna-decide-reject", drawer);
        Assert.DoesNotContain("cna-decide-approve", drawer);
        Assert.Contains("e.g. Already credited as credit note 61455", drawer);
    }

    /// <summary>
    /// The production case: its stage does not list the app's SAP user, so nothing can be decided here. The
    /// drawer says what to do in SAP, once — not again in the generic note at the foot.
    /// </summary>
    [Fact]
    public async Task A_draft_the_app_cannot_decide_says_to_remove_it_in_sap_once()
    {
        var (_, drawer) = await RenderAsync(Item(canDecide: false));

        Assert.Contains("Already credited as credit note 61455", drawer);
        Assert.Contains("Remove the draft in SAP.", drawer);
        Assert.DoesNotContain("Reject it", drawer);
        Assert.DoesNotContain("cna-decide", drawer);
        Assert.DoesNotContain("cna-blocked", drawer);
    }

    private static CreditNoteApprovalListItemDto Item(bool canDecide) => new()
    {
        Code = Code,
        Status = "Pending",
        StageName = "Production WashBay",
        DraftEntry = 81520,
        DraftNum = 61450,
        CardCode = "C00077",
        CardName = Customer,
        NumAtCard = "800123",
        DocDate = new DateTime(2026, 8, 15),
        DocTotal = 412.65m,
        DocCurrency = "USD",
        DraftAuthorizationStatus = "Pending",
        DraftIsOpen = true,
        CanDecide = canDecide,
        CanApprove = false,
        CanAdd = false,
        StatusNote = Note(canReject: canDecide),
        AlreadyCredited = new CreditNoteApprovalAlreadyCreditedDto
        {
            DocEntry = 70310,
            DocNum = 61455,
            EnteredDate = new DateTime(2026, 8, 15),
            EnteredByUserCode = "AR1",
            EnteredByName = "AR1"
        }
    };

    /// <summary>Renders the page with one request, opens it, and returns the list and the drawer's HTML.</summary>
    private static async Task<(string List, string Drawer)> RenderAsync(CreditNoteApprovalListItemDto item)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddSingleton<IMediator>(Mediator(item));
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

        var list = await renderer.Dispatcher.InvokeAsync(output.ToHtmlString);

        await renderer.Dispatcher.InvokeAsync(() => Invoke(page!, "OpenAsync", Code));
        var drawer = await renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.Contains("cna-drawer", drawer);

        return (list, drawer);
    }

    private static IMediator Mediator(CreditNoteApprovalListItemDto item) =>
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
                GetCreditNoteApprovalQuery => Task.FromResult<ErrorOr<CreditNoteApprovalDetailDto>>(new CreditNoteApprovalDetailDto { Request = item }),
                _ => throw new InvalidOperationException($"{request?.GetType().Name} was not expected.")
            }
            : throw new InvalidOperationException($"IMediator.{method.Name} was not expected."));
}
