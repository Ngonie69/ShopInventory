using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.DailyIncomingPayments;
using ShopInventory.Web.Features.DailyIncomingPayments.Commands.SaveIncomingPaymentGlMapping;
using ShopInventory.Web.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayment;
using ShopInventory.Web.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayments;
using ShopInventory.Web.Features.DailyIncomingPayments.Queries.GetIncomingPaymentGlMappings;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The daily incoming payments that settle till, vending and van invoices, and the per-partner G/L
/// accounts they post to.
/// </summary>
/// <remarks>
/// Everyone who reads desktop sales can see both tabs. Only an admin can change an account, because
/// a wrong account moves real money to the wrong ledger; the API enforces the same split.
/// </remarks>
public partial class DailyIncomingPayments
{
    private enum Tab { Payments, Accounts }

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IDbContextFactory<WebAppDbContext> DbContextFactory { get; set; } = default!;
    [Inject] private ILogger<DailyIncomingPayments> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    private Tab tab = Tab.Payments;
    private bool isAdmin;
    private bool isLoading = true;
    private bool isSaving;
    private string? error;

    private DateTime? fromDate = Today().AddDays(-6);
    private DateTime? toDate = Today();
    private string? partnerFilter;
    private string? statusFilter;

    private List<DailyIncomingPaymentSummary> payments = [];
    private List<IncomingPaymentGlMapping> mappings = [];

    private int? openId;
    private DailyIncomingPaymentDetail? detail;

    private List<NocturnePickerOption> accountOptions = [];
    private Dictionary<string, string> accountNames = new(StringComparer.OrdinalIgnoreCase);
    private bool accountsLoading = true;

    private MappingForm? editing;
    private bool isNew;

    private static readonly NocturneSelectOption<string>[] StatusOptions =
    [
        new(string.Empty, "All statuses") { RuleAfter = true, IsUnset = true },
        new("Posted", "Posted", "good"),
        new("Pending", "Pending", "warn"),
        new("Unresolved", "Unresolved", "bad"),
        new("NothingToPay", "Nothing to pay", "neutral"),
        new("Released", "Released", "neutral")
    ];

    private IReadOnlyList<NocturneSelectOption<string>> PartnerOptions =>
    [
        new(string.Empty, "All partners") { RuleAfter = true, IsUnset = true },
        .. mappings
            .Select(mapping => mapping.CardCode)
            .Concat(payments.Select(payment => payment.CardCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(code => new NocturneSelectOption<string>(code, code))
    ];

    private string PaymentsLabel => payments.Count == 1 ? "1 payment" : $"{payments.Count} payments";

    protected override async Task OnInitializedAsync()
    {
        if (AuthState is not null)
        {
            var state = await AuthState;
            isAdmin = state.User.IsInRole("Admin");
        }

        await Task.WhenAll(LoadAccountsAsync(), ReloadAsync());
    }

    private async Task ReloadAsync()
    {
        isLoading = true;
        error = null;
        await Task.WhenAll(LoadPaymentsCoreAsync(), LoadMappingsAsync());
        isLoading = false;
    }

    private async Task LoadPaymentsAsync()
    {
        isLoading = true;
        await LoadPaymentsCoreAsync();
        isLoading = false;
    }

    private async Task LoadPaymentsCoreAsync()
    {
        openId = null;
        detail = null;

        var from = fromDate ?? Today().AddDays(-6);
        var to = toDate ?? Today();
        if (to < from)
        {
            (from, to) = (to, from);
        }

        var result = await Mediator.Send(new GetDailyIncomingPaymentsQuery(from, to, partnerFilter, statusFilter));
        if (result.IsError)
        {
            error = result.FirstError.Description;
            payments = [];
            return;
        }

        payments = result.Value;
    }

    private async Task LoadMappingsAsync()
    {
        var result = await Mediator.Send(new GetIncomingPaymentGlMappingsQuery());
        if (result.IsError)
        {
            error = result.FirstError.Description;
            return;
        }

        mappings = result.Value;
    }

    /// <summary>
    /// The G/L account list for the pickers, from the Web's SAP cache. Only for choosing and naming: the
    /// API checks every saved account against SAP itself.
    /// </summary>
    private async Task LoadAccountsAsync()
    {
        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();
            var accounts = await db.CachedGLAccounts
                .AsNoTracking()
                .Where(account => account.IsActive)
                .OrderBy(account => account.Code)
                .Select(account => new { account.Code, account.Name })
                .ToListAsync();

            accountNames = accounts.ToDictionary(
                account => account.Code, account => account.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            accountOptions = accounts
                .Select(account => new NocturnePickerOption(account.Code, string.IsNullOrWhiteSpace(account.Name) ? account.Code : account.Name))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load the cached G/L accounts for the daily payment page");
        }
        finally
        {
            accountsLoading = false;
        }
    }

    private void SetTab(Tab next)
    {
        tab = next;
        error = null;
    }

    private async Task OnFromChanged(DateTime? value)
    {
        fromDate = value;
        await LoadPaymentsAsync();
    }

    private async Task OnToChanged(DateTime? value)
    {
        toDate = value;
        await LoadPaymentsAsync();
    }

    private async Task ToggleAsync(DailyIncomingPaymentSummary payment)
    {
        if (openId == payment.Id)
        {
            openId = null;
            detail = null;
            return;
        }

        openId = payment.Id;
        detail = null;

        var result = await Mediator.Send(new GetDailyIncomingPaymentQuery(payment.Id));
        if (openId != payment.Id)
        {
            return;
        }

        if (result.IsError)
        {
            error = result.FirstError.Description;
            openId = null;
            return;
        }

        detail = result.Value;
    }

    // ---- Editing a mapping ------------------------------------------------------------------------

    private void StartNew()
    {
        error = null;
        isNew = true;
        editing = new MappingForm { Run = "Shops", IsActive = true };
    }

    private void StartEdit(IncomingPaymentGlMapping mapping)
    {
        error = null;
        isNew = false;
        editing = new MappingForm
        {
            CardCode = mapping.CardCode,
            CardName = mapping.CardName,
            CashAccount = mapping.CashAccount,
            ElectronicAccount = mapping.ElectronicAccount,
            Run = mapping.Run,
            NotifyEmails = mapping.NotifyEmails,
            IsActive = mapping.IsActive
        };
    }

    private void CloseDialog()
    {
        if (isSaving)
        {
            return;
        }

        editing = null;
        error = null;
    }

    private async Task SaveAsync()
    {
        if (editing is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(editing.CardCode)
            || string.IsNullOrWhiteSpace(editing.CashAccount)
            || string.IsNullOrWhiteSpace(editing.ElectronicAccount))
        {
            error = "A partner code and both G/L accounts are required.";
            return;
        }

        isSaving = true;
        error = null;

        try
        {
            var result = await Mediator.Send(new SaveIncomingPaymentGlMappingCommand(
                editing.CardCode.Trim(),
                new SaveIncomingPaymentGlMappingBody(
                    editing.CardName,
                    editing.CashAccount,
                    editing.ElectronicAccount,
                    editing.Run,
                    editing.NotifyEmails,
                    editing.IsActive)));

            if (result.IsError)
            {
                error = result.FirstError.Description;
                return;
            }

            editing = null;
            await LoadMappingsAsync();
        }
        finally
        {
            isSaving = false;
        }
    }

    // ---- Display --------------------------------------------------------------------------------------

    private string AccountName(string code) =>
        accountNames.TryGetValue(code, out var name) ? name : string.Empty;

    private static string StatusLabel(DailyIncomingPaymentSummary payment) => payment.Status switch
    {
        "Pending" when payment.LastError?.StartsWith("No active G/L mapping", StringComparison.Ordinal) == true => "Held: no G/L",
        "NothingToPay" => "Nothing to pay",
        _ => payment.Status
    };

    private static string StatusChip(DailyIncomingPaymentSummary payment) => payment.Status switch
    {
        "Posted" => "dpy-chip-good",
        "Pending" => "dpy-chip-warn",
        "Unresolved" => "dpy-chip-bad",
        _ => "dpy-chip-neutral"
    };

    private static string Money(decimal value) => value.ToString("N2", Invariant);

    private static string ToCat(DateTime utc) => IAuditService.ToCAT(
            utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc))
        .ToString("dd MMM yyyy HH:mm", Invariant);

    private static DateTime Today() =>
        IAuditService.ToCAT(DateTime.UtcNow).Date;

    /// <summary>The dialog's working copy.</summary>
    private sealed class MappingForm
    {
        public string CardCode { get; set; } = string.Empty;
        public string? CardName { get; set; }
        public string? CashAccount { get; set; }
        public string? ElectronicAccount { get; set; }
        public string Run { get; set; } = "Shops";
        public string? NotifyEmails { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
