using System.Net;
using System.Security.Claims;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApproval;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalOriginators;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;
using WebErrors = ShopInventory.Web.Common.Errors.Errors;

namespace ShopInventory.Tests;

/// <summary>
/// /credit-notes/approvals as a decision queue, rendered for real: the tiles carry SAP's counts, and a
/// decision moves the drawer on to the next request instead of leaving it on the one just decided.
/// </summary>
/// <remarks>
/// Rendered with the add page tests' renderer, which mounts the interactive page in place and calls
/// handlers the way a click does.
/// </remarks>
public sealed class CreditNoteApprovalQueuePageTests
{
    [Fact]
    public async Task The_queue_tiles_carry_the_counts_sap_gave()
    {
        var sap = new FakeQueue([Request(101, "Corner Grocer Avenues")], new CreditNoteApprovalCountsDto { Pending = 12, Approved = 41, Rejected = 3 });

        var (_, html) = await RenderAsync(sap, _ => Task.CompletedTask);

        Assert.Contains("<span class=\"cna-queue-count\">12</span>", html);
        Assert.Contains("<span class=\"cna-queue-count\">41</span>", html);
        Assert.Contains("<span class=\"cna-queue-count\">3</span>", html);

        // The page opens on the requests still waiting for somebody, not the API's default mix.
        Assert.Equal("pending", Assert.Single(sap.ListStatuses.Distinct()));
        Assert.Equal(1, sap.CountReads);
    }

    [Fact]
    public async Task Raised_by_and_dates_narrow_the_list_and_the_tiles_in_sap()
    {
        var sap = new FakeQueue([Request(101, "Corner Grocer Avenues")], new CreditNoteApprovalCountsDto { Pending = 12 });

        var (_, html) = await RenderAsync(sap, async page =>
        {
            // Typed the wrong way round; the range they describe is what is read.
            SetField(page, "raisedFrom", (DateTime?)new DateTime(2026, 11, 30));
            SetField(page, "raisedTo", (DateTime?)new DateTime(2026, 11, 1));
            SetField(page, "originatorFilter", "31");
            await CreditNoteApprovalAddPageTests.Invoke(page, "OnSapFilterChangedAsync");
        });

        var expected = new CreditNoteApprovalFilter(new DateTime(2026, 11, 1), new DateTime(2026, 11, 30), 31);
        Assert.Equal([null, expected], sap.ListFilters);
        Assert.Equal([null, expected], sap.CountFilters);

        // The trigger names the person chosen, and the row offers a way back.
        Assert.Contains("Tendai Banda", html);
        Assert.Contains("Clear filters", html);
    }

    [Fact]
    public async Task An_unchanged_filter_reads_nothing_and_clearing_reads_the_whole_queue_again()
    {
        var sap = new FakeQueue([Request(101, "Corner Grocer Avenues")], new CreditNoteApprovalCountsDto { Pending = 12 });

        var (_, html) = await RenderAsync(sap, async page =>
        {
            SetField(page, "originatorFilter", "31");
            await CreditNoteApprovalAddPageTests.Invoke(page, "OnSapFilterChangedAsync");
            await CreditNoteApprovalAddPageTests.Invoke(page, "OnSapFilterChangedAsync");
            await CreditNoteApprovalAddPageTests.Invoke(page, "ClearFiltersAsync");
        });

        var byPerson = new CreditNoteApprovalFilter(OriginatorId: 31);
        Assert.Equal([null, byPerson, null], sap.ListFilters);
        Assert.Equal([null, byPerson, null], sap.CountFilters);
        Assert.DoesNotContain("Clear filters", html);
    }

    [Fact]
    public async Task Counts_that_cannot_be_read_leave_dashes_and_the_rows_alone()
    {
        var sap = new FakeQueue([Request(101, "Corner Grocer Avenues")], counts: null);

        var (_, html) = await RenderAsync(sap, _ => Task.CompletedTask);

        Assert.DoesNotContain("<span class=\"cna-queue-count\">…</span>", html);
        Assert.Contains("<span class=\"cna-queue-count\">—</span>", html);
        Assert.Contains("Corner Grocer Avenues", html);
        Assert.DoesNotContain("cna-alert", html);
    }

    [Fact]
    public async Task Approving_moves_the_drawer_to_the_next_request_and_keeps_what_sap_said()
    {
        var sap = new FakeQueue(
            [Request(103, "Eastgate Superette"), Request(102, "Riverside Wholesale"), Request(101, "Hillside Mini Mart")],
            new CreditNoteApprovalCountsDto { Pending = 3 });

        var (page, html) = await RenderAsync(sap, async page =>
        {
            await CreditNoteApprovalAddPageTests.Invoke(page, "OpenAsync", 103);
            await CreditNoteApprovalAddPageTests.Invoke(page, "ApproveAsync");
        });

        Assert.Equal([(103, "Approved")], sap.Decisions);

        // On to the next row, which is what the manager works through next.
        Assert.Contains("<h2 class=\"cna-drawer-title\">Request 102</h2>", html);
        Assert.Contains("1 of 2", html);

        // What SAP said about 103 stays on the page, named, though the drawer has moved on.
        Assert.Contains("cna-done cna-added", html);
        Assert.Contains("Request 103 approved.", html);
        Assert.Contains("Approval request 103 · Eastgate Superette", html);

        // The decided row has left the queue, and the tiles were re-counted.
        Assert.DoesNotContain("Eastgate Superette</span>", html);
        Assert.Equal(2, sap.CountReads);
    }

    [Fact]
    public async Task Deciding_the_last_request_closes_the_drawer()
    {
        var sap = new FakeQueue([Request(101, "Hillside Mini Mart")], new CreditNoteApprovalCountsDto { Pending = 1 });

        var (_, html) = await RenderAsync(sap, async page =>
        {
            await CreditNoteApprovalAddPageTests.Invoke(page, "OpenAsync", 101);
            await CreditNoteApprovalAddPageTests.Invoke(page, "RejectAsync");
        });

        Assert.Equal([(101, "NotApproved")], sap.Decisions);
        Assert.DoesNotContain("cna-drawer", html);
        Assert.Contains("Request 101 rejected.", html);
        Assert.Contains("Nothing is waiting for a decision in SAP", html);
    }

    [Fact]
    public async Task A_refused_decision_keeps_the_drawer_on_the_same_request_with_the_reason()
    {
        const string refusal = "SAP refused the decision: this stage has already been decided.";
        var sap = new FakeQueue(
            [Request(103, "Eastgate Superette"), Request(102, "Riverside Wholesale")],
            new CreditNoteApprovalCountsDto { Pending = 2 },
            refusal);

        var (_, html) = await RenderAsync(sap, async page =>
        {
            await CreditNoteApprovalAddPageTests.Invoke(page, "OpenAsync", 103);
            await CreditNoteApprovalAddPageTests.Invoke(page, "ApproveAsync");
        });

        Assert.Contains("<h2 class=\"cna-drawer-title\">Request 103</h2>", html);
        Assert.Contains(refusal, html);
        Assert.DoesNotContain("cna-added", html);
    }

    [Fact]
    public async Task The_drawer_steps_through_the_rows_on_screen()
    {
        var sap = new FakeQueue(
            [Request(103, "Eastgate Superette"), Request(102, "Riverside Wholesale"), Request(101, "Hillside Mini Mart")],
            new CreditNoteApprovalCountsDto { Pending = 3 });

        var (_, html) = await RenderAsync(sap, async page =>
        {
            await CreditNoteApprovalAddPageTests.Invoke(page, "OpenAsync", 102);
            await CreditNoteApprovalAddPageTests.Invoke(page, "OpenNextAsync");
        });

        Assert.Contains("<h2 class=\"cna-drawer-title\">Request 101</h2>", html);
        Assert.Contains("3 of 3", html);
        Assert.Empty(sap.Decisions);
    }

    /// <summary>Renders the page, runs <paramref name="act"/> against it, and returns the page's HTML.</summary>
    private static void SetField(CreditNoteApprovals page, string name, object? value) =>
        (typeof(CreditNoteApprovals).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"CreditNoteApprovals.{name} no longer exists."))
        .SetValue(page, value);

    private static async Task<(CreditNoteApprovals Page, string Html)> RenderAsync(
        FakeQueue sap,
        Func<CreditNoteApprovals, Task> act)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddSingleton<IMediator>(sap.Mediator());
        services.AddSingleton<ISnackbar>(new CreditNoteApprovalAddPageTests.SilentSnackbar());
        services.AddSingleton(StubProxy.Unused<IAuditService>());
        services.AddSingleton(StubProxy.Unused<IJSRuntime>());

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new CreditNoteApprovalAddPageTests.InteractivePageRenderer(provider);

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
        await renderer.Dispatcher.InvokeAsync(() => act(page!));
        // Decoded, so the assertions read the text a person sees: the renderer escapes · and —.
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(output.ToHtmlString));
        return (page!, html);
    }

    private static CreditNoteApprovalListItemDto Request(int code, string customer) => new()
    {
        Code = code,
        Status = "Pending",
        StageName = "Factory Washbay",
        TemplateName = "Washbay Credit Notes",
        OriginatorName = "Rose",
        OriginatorUserCode = "Inv",
        DraftEntry = code + 60000,
        DraftNum = code + 60000,
        CardCode = "CUS" + code,
        CardName = customer,
        CreatedDate = new DateTime(2026, 11, 5),
        DocDate = new DateTime(2026, 11, 5),
        DocTotal = 531.30m,
        DocCurrency = "USD",
        Comments = "Crates returned on delivery.",
        DraftIsOpen = true,
        CanDecide = true
    };

    /// <summary>
    /// The pending queue as SAP keeps it: a decided request leaves it, and a refused decision leaves
    /// it as it was.
    /// </summary>
    private sealed class FakeQueue(
        List<CreditNoteApprovalListItemDto> pending,
        CreditNoteApprovalCountsDto? counts,
        string? refusal = null)
    {
        public List<string?> ListStatuses { get; } = [];
        public List<CreditNoteApprovalFilter?> ListFilters { get; } = [];
        public List<CreditNoteApprovalFilter?> CountFilters { get; } = [];
        public List<(int Code, string Decision)> Decisions { get; } = [];
        public int CountReads { get; private set; }

        public IMediator Mediator() =>
            StubProxy.For<IMediator>((method, args) => method.Name == nameof(IMediator.Send) && args is [var request, ..]
                ? Answer(request)
                : throw new InvalidOperationException($"IMediator.{method.Name} was not expected."));

        private object Answer(object? request)
        {
            switch (request)
            {
                case GetCreditNoteApprovalsQuery list:
                    ListStatuses.Add(list.Status);
                    ListFilters.Add(list.Filter);
                    return Task.FromResult<ErrorOr<CreditNoteApprovalListResponseDto>>(new CreditNoteApprovalListResponseDto
                    {
                        Items = pending.ToList(),
                        TotalCount = pending.Count,
                        Page = 1,
                        PageSize = 25,
                        Status = list.Status ?? "open"
                    });

                case GetCreditNoteApprovalCountsQuery countQuery:
                    CountReads++;
                    CountFilters.Add(countQuery.Filter);
                    return Task.FromResult<ErrorOr<CreditNoteApprovalCountsDto>>(counts is null
                        ? WebErrors.CreditNoteApproval.LoadFailed("The queue counts could not be read from SAP.")
                        : counts);

                case GetCreditNoteApprovalOriginatorsQuery:
                    return Task.FromResult<ErrorOr<List<CreditNoteApprovalOriginatorDto>>>(new List<CreditNoteApprovalOriginatorDto>
                    {
                        new() { Id = 31, UserCode = "tbanda", Name = "Tendai Banda" },
                        new() { Id = 15, UserCode = "washbay" }
                    });

                case GetCreditNoteApprovalQuery detail:
                    var row = pending.FirstOrDefault(item => item.Code == detail.Code);
                    return Task.FromResult<ErrorOr<CreditNoteApprovalDetailDto>>(row is null
                        ? WebErrors.CreditNoteApproval.LoadFailed($"SAP approval request {detail.Code} was not found.")
                        : new CreditNoteApprovalDetailDto { Request = row });

                case DecideCreditNoteApprovalCommand decide:
                    if (refusal is not null)
                    {
                        return Task.FromResult<ErrorOr<CreditNoteApprovalDecisionResultDto>>(
                            WebErrors.CreditNoteApproval.DecisionFailed(refusal));
                    }

                    Decisions.Add((decide.Code, decide.Decision));
                    pending.RemoveAll(item => item.Code == decide.Code);
                    var approved = decide.Decision == "Approved";
                    return Task.FromResult<ErrorOr<CreditNoteApprovalDecisionResultDto>>(new CreditNoteApprovalDecisionResultDto
                    {
                        Code = decide.Code,
                        Decision = decide.Decision,
                        Status = approved ? "Approved" : "NotApproved",
                        CanAdd = approved,
                        Message = approved ? $"Request {decide.Code} approved." : $"Request {decide.Code} rejected."
                    });

                default:
                    throw new InvalidOperationException($"{request?.GetType().Name} was not expected.");
            }
        }
    }
}
