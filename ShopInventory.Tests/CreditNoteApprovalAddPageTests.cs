using System.Reflection;
using System.Security.Claims;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Features.CreditNoteApprovals.Commands.AddApprovedCreditNote;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApproval;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;
using WebErrors = ShopInventory.Web.Common.Errors.Errors;

namespace ShopInventory.Tests;

/// <summary>
/// What /credit-notes/approvals shows after "Add credit note", rendered for real against a stand-in
/// for SAP that behaves as SAP does.
/// </summary>
/// <remarks>
/// A successful <c>DraftsService_SaveDraftToDocument</c> deletes the approval request (measured in
/// KEFALOS_TEST_3; see docs/sap-credit-note-approvals.md). The page used to re-read the request after
/// every add, success included, so a good add ended with the drawer showing "SAP approval request N
/// was not found" under a success snackbar that faded in seconds. The stand-in here answers that
/// re-read the way the API does once the request is gone, so the old page fails these tests.
/// </remarks>
public sealed class CreditNoteApprovalAddPageTests
{
    private const int Code = 4182;
    private const string Customer = "Greenfield Mini Mart";
    private const string NotFound = "SAP approval request 4182 was not found, or it is not for an A/R credit memo.";

    [Fact]
    public async Task A_successful_add_closes_the_drawer_and_keeps_what_it_said_on_the_page()
    {
        var sap = new FakeApprovalQueue(new AddApprovedCreditNoteResultDto
        {
            Code = Code,
            CreditNoteDocNum = 55301,
            Resolved = true,
            Fiscalisation = new CreditNoteApprovalFiscalisationDto { Attempted = true, Success = true },
            Message = "Credit note #55301 added to SAP. Fiscalised."
        });

        var html = await OpenThenAddAsync(sap);

        Assert.Equal(1, sap.Adds);
        Assert.DoesNotContain("was not found", html);
        Assert.DoesNotContain("cna-drawer", html);

        Assert.Contains("cna-done cna-added", html);
        Assert.Contains("Credit note #55301 added to SAP. Fiscalised.", html);
        Assert.Contains($"From approval request {Code}", html);
        Assert.Contains(Customer, html);

        // The request is gone from SAP, so it is not read again; the list is, and the row has left it.
        Assert.Equal(1, sap.DetailReads);
        Assert.True(sap.ListReads >= 2, $"The list was read {sap.ListReads} time(s); the add should re-read it.");
    }

    [Fact]
    public async Task An_add_whose_fiscalisation_failed_keeps_the_notice_in_the_attention_colour()
    {
        var sap = new FakeApprovalQueue(new AddApprovedCreditNoteResultDto
        {
            Code = Code,
            CreditNoteDocNum = 55302,
            Resolved = true,
            Fiscalisation = new CreditNoteApprovalFiscalisationDto { Attempted = true, Success = false, Message = "device offline" },
            Message = "Credit note #55302 added to SAP. Fiscalisation failed and has been logged for review: device offline"
        });

        var html = await OpenThenAddAsync(sap);

        Assert.DoesNotContain("was not found", html);
        Assert.DoesNotContain("cna-drawer", html);
        Assert.Contains("cna-blocked cna-added", html);
        Assert.Contains("Fiscalisation failed and has been logged for review: device offline", html);
    }

    [Fact]
    public async Task A_refused_add_keeps_the_drawer_open_with_the_reason()
    {
        const string refusal = "SAP refused the add: Cannot add row without complete selection of batch/serial numbers.";
        var sap = new FakeApprovalQueue(added: null, refusal: refusal);

        var html = await OpenThenAddAsync(sap);

        Assert.Equal(1, sap.Adds);
        Assert.Contains("cna-drawer", html);
        Assert.Contains(refusal, html);
        Assert.DoesNotContain("cna-added", html);

        // Nothing changed in SAP, so the drawer re-reads the request and still shows it.
        Assert.Equal(2, sap.DetailReads);
    }

    /// <summary>
    /// The wash bay counted 80 white crates where the draft claims 100. The drawer shows the change and
    /// what it does to the total, and the add carries the new quantity — and only the changed line.
    /// </summary>
    [Fact]
    public async Task A_changed_quantity_shows_in_the_drawer_and_goes_with_the_add()
    {
        var sap = new FakeApprovalQueue(new AddApprovedCreditNoteResultDto
        {
            Code = Code,
            CreditNoteDocNum = 55303,
            Resolved = true,
            Message = "Credit note #55303 added to SAP. Quantities changed before adding: CRA001 100 → 80."
        })
        { Lines = CrateLines() };

        string? edited = null;
        await OpenThenAddAsync(sap, role: "WashBay", beforeAdd: async (renderer, page, output) =>
        {
            Assert.Contains("cna-qty-input", await renderer.Dispatcher.InvokeAsync(output.ToHtmlString));

            await renderer.Dispatcher.InvokeAsync(() => InvokeSync(page, "SetQuantity", CrateLines()[0], "80"));
            edited = await renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        });

        Assert.NotNull(edited);
        Assert.Contains("was 100", edited);
        Assert.Contains("1 quantity changed", edited);
        // 412.60 less 20 crates at 1.15 gross each.
        Assert.Contains("389.60", edited);

        var sent = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<CreditNoteLineQuantityDto>>(sap.LastAdd!.LineQuantities));
        Assert.Equal((0, 80m), (sent.LineNum, sent.Quantity));
    }

    /// <summary>A quantity of nothing cannot be added: the button is off and the confirm will not open.</summary>
    [Fact]
    public async Task A_zero_quantity_holds_the_add_back()
    {
        var sap = new FakeApprovalQueue(added: null) { Lines = CrateLines() };

        string? edited = null;
        await OpenThenAddAsync(sap, role: "WashBay", press: false, beforeAdd: async (renderer, page, output) =>
        {
            await renderer.Dispatcher.InvokeAsync(() => InvokeSync(page, "SetQuantity", CrateLines()[0], "0"));
            await renderer.Dispatcher.InvokeAsync(() => InvokeSync(page, "OpenAddConfirm"));
            edited = await renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        });

        Assert.Contains("is-invalid", edited);
        Assert.Contains("Every quantity must be above 0", edited);
        Assert.DoesNotContain("cna-dialog", edited);
        Assert.Equal(0, sap.Adds);
    }

    private static List<CreditNoteDraftLineDto> CrateLines() =>
    [
        new() { LineNum = 0, ItemCode = "CRA001", ItemDescription = "Crates White Yoghurt", Quantity = 100m, UnitPrice = 1m, LineTotal = 100m, VatSum = 15m },
        new() { LineNum = 1, ItemCode = "CRA002", ItemDescription = "Crates Black Yoghurt", Quantity = 50m, UnitPrice = 1m, LineTotal = 50m, VatSum = 7.5m }
    ];

    /// <summary>Renders the page, opens request <see cref="Code"/>, presses Add, and returns the HTML.</summary>
    /// <remarks>
    /// HtmlRenderer cannot click, so the two handlers the drawer and the confirm dialog bind are called
    /// on the renderer's dispatcher, as a click would run them. They are found by name; a rename fails
    /// here with that name rather than passing quietly.
    /// </remarks>
    private static async Task<string> OpenThenAddAsync(
        FakeApprovalQueue sap,
        string role = "Admin",
        bool press = true,
        Func<InteractivePageRenderer, CreditNoteApprovals, Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent, Task>? beforeAdd = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddSingleton<IMediator>(sap.Mediator());
        services.AddSingleton<ISnackbar>(new SilentSnackbar());
        services.AddSingleton(StubProxy.Unused<IAuditService>());
        services.AddSingleton(StubProxy.Unused<IJSRuntime>());

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new InteractivePageRenderer(provider);

        CreditNoteApprovals? page = null;
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"));
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
        Assert.Contains(Customer, drawer);

        if (beforeAdd is not null)
        {
            await beforeAdd(renderer, page!, output);
        }

        if (press)
        {
            await renderer.Dispatcher.InvokeAsync(() => Invoke(page!, "AddAsync"));
        }

        return await renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
    }

    /// <summary>Runs a synchronous handler the way a click or a keystroke does, then re-renders.</summary>
    internal static void InvokeSync(CreditNoteApprovals page, string name, params object[] args)
    {
        var method = typeof(CreditNoteApprovals).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"CreditNoteApprovals.{name} no longer exists.");
        method.Invoke(page, args);

        typeof(ComponentBase)
            .GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, null);
    }

    /// <summary>
    /// A static renderer that mounts an <c>@rendermode InteractiveServer</c> page in place, which
    /// <see cref="HtmlRenderer"/> refuses to do.
    /// </summary>
    internal sealed class InteractivePageRenderer(IServiceProvider services)
        : StaticHtmlRenderer(services, NullLoggerFactory.Instance)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type componentType,
            int? parentComponentId,
            IComponentActivator componentActivator,
            IComponentRenderMode renderMode) =>
            componentActivator.CreateInstance(componentType);
    }

    /// <summary>
    /// Runs a handler the way a click does: await it, then re-render, as
    /// <c>ComponentBase.HandleEventAsync</c> does once an event handler's task completes.
    /// </summary>
    internal static async Task Invoke(CreditNoteApprovals page, string name, params object[] args)
    {
        var method = typeof(CreditNoteApprovals).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"CreditNoteApprovals.{name} no longer exists.");
        await (Task)method.Invoke(page, args)!;

        typeof(ComponentBase)
            .GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, null);
    }

    /// <summary>
    /// The approval queue as SAP keeps it: one approved request until it is added, after which a
    /// successful add deletes it and reading it answers what the API answers then.
    /// </summary>
    private sealed class FakeApprovalQueue(AddApprovedCreditNoteResultDto? added, string? refusal = null)
    {
        private bool deleted;

        public int ListReads { get; private set; }
        public int DetailReads { get; private set; }
        public int Adds { get; private set; }

        /// <summary>The draft's lines, which the drawer shows.</summary>
        public List<CreditNoteDraftLineDto> Lines { get; init; } = [];

        public AddApprovedCreditNoteCommand? LastAdd { get; private set; }

        public IMediator Mediator() =>
            StubProxy.For<IMediator>((method, args) => method.Name == nameof(IMediator.Send) && args is [var request, ..]
                ? Answer(request)
                : throw new InvalidOperationException($"IMediator.{method.Name} was not expected."));

        private object Answer(object? request)
        {
            switch (request)
            {
                case GetCreditNoteApprovalsQuery:
                    ListReads++;
                    return Task.FromResult<ErrorOr<CreditNoteApprovalListResponseDto>>(new CreditNoteApprovalListResponseDto
                    {
                        Items = deleted ? [] : [Item()],
                        TotalCount = deleted ? 0 : 1,
                        Page = 1,
                        PageSize = 25,
                        Status = "open"
                    });

                case GetCreditNoteApprovalCountsQuery:
                    return Task.FromResult<ErrorOr<CreditNoteApprovalCountsDto>>(
                        new CreditNoteApprovalCountsDto { Approved = deleted ? 0 : 1 });

                case ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalOriginators.GetCreditNoteApprovalOriginatorsQuery:
                    return Task.FromResult<ErrorOr<List<CreditNoteApprovalOriginatorDto>>>(new List<CreditNoteApprovalOriginatorDto>());

                case GetCreditNoteApprovalQuery:
                    DetailReads++;
                    return Task.FromResult<ErrorOr<CreditNoteApprovalDetailDto>>(deleted
                        ? WebErrors.CreditNoteApproval.LoadFailed(NotFound)
                        : new CreditNoteApprovalDetailDto { Request = Item(), Lines = Lines });

                case AddApprovedCreditNoteCommand add:
                    Adds++;
                    LastAdd = add;
                    if (added is null)
                    {
                        return Task.FromResult<ErrorOr<AddApprovedCreditNoteResultDto>>(
                            WebErrors.CreditNoteApproval.AddFailed(refusal ?? "refused"));
                    }

                    deleted = true;
                    return Task.FromResult<ErrorOr<AddApprovedCreditNoteResultDto>>(added);

                default:
                    throw new InvalidOperationException($"{request?.GetType().Name} was not expected.");
            }
        }

        private static CreditNoteApprovalListItemDto Item() => new()
        {
            Code = Code,
            Status = "Approved",
            StageName = "Factory Washbay",
            DraftEntry = 10563,
            DraftNum = 10563,
            CardCode = "C00418",
            CardName = Customer,
            DocDate = new DateTime(2026, 11, 6),
            DocTotal = 412.60m,
            DocCurrency = "USD",
            DraftAuthorizationStatus = "dasApproved",
            DraftIsOpen = true,
            CanAdd = true
        };
    }

    internal sealed class SilentSnackbar : ISnackbar
    {
        public IEnumerable<Snackbar> ShownSnackbars => [];

        public SnackbarConfiguration Configuration { get; } = new();

        public event Action? OnSnackbarsUpdated { add { } remove { } }

        public Snackbar? Add(string message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null) => null;

        public Snackbar? Add(MarkupString message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null) => null;

        public Snackbar? Add(RenderFragment message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null) => null;

        public Snackbar? Add<T>(Dictionary<string, object>? componentParameters = null, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null)
            where T : IComponent => null;

        public void Clear() { }

        public void Remove(Snackbar snackbar) { }

        public void RemoveByKey(string key) { }

        public void Dispose() { }
    }
}
