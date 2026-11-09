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

    /// <summary>Renders the page, opens request <see cref="Code"/>, presses Add, and returns the HTML.</summary>
    /// <remarks>
    /// HtmlRenderer cannot click, so the two handlers the drawer and the confirm dialog bind are called
    /// on the renderer's dispatcher, as a click would run them. They are found by name; a rename fails
    /// here with that name rather than passing quietly.
    /// </remarks>
    private static async Task<string> OpenThenAddAsync(FakeApprovalQueue sap)
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
        Assert.Contains(Customer, drawer);

        await renderer.Dispatcher.InvokeAsync(() => Invoke(page!, "AddAsync"));
        return await renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
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

                case GetCreditNoteApprovalQuery:
                    DetailReads++;
                    return Task.FromResult<ErrorOr<CreditNoteApprovalDetailDto>>(deleted
                        ? WebErrors.CreditNoteApproval.LoadFailed(NotFound)
                        : new CreditNoteApprovalDetailDto { Request = Item() });

                case AddApprovedCreditNoteCommand:
                    Adds++;
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
