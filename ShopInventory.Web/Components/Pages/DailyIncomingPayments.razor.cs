using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
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

    private const string ShopsRun = "Shops";
    private const string VansRun = "Vans";

    /// <summary>
    /// The API's <c>DesktopSalePosting:DailyPaymentTimeCAT</c> and <c>VanDailyPaymentTimeCAT</c> defaults. The
    /// API does not publish its settings, and Settings › Payments states the same times.
    /// </summary>
    private static readonly TimeSpan ShopsRunTime = new(17, 0, 0);
    private static readonly TimeSpan VansRunTime = new(20, 0, 0);

    /// <summary>SAP's default cash account: where a payment with no accounts would land.</summary>
    private const string SapDefaultCashAccount = "700300";

    /// <summary>The prefix the API writes on a payment it holds for want of a G/L mapping.</summary>
    private const string HeldForMappingPrefix = "No active G/L mapping";

    private const string SettingsHref = "/settings?section=payments";
    private const int AttentionCards = 6;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] RunOrder = [ShopsRun, VansRun];
    private static readonly string?[] RunFilterOptions = [null, ShopsRun, VansRun];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IDbContextFactory<WebAppDbContext> DbContextFactory { get; set; } = default!;
    [Inject] private IDailyIncomingPaymentSettingsService PaymentSettings { get; set; } = default!;
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
    private string? runFilter;

    private List<DailyIncomingPaymentSummary> payments = [];
    private List<IncomingPaymentGlMapping> mappings = [];
    private Dictionary<string, IncomingPaymentGlMapping> mappingsByCode = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the switch could not be read, which is not the same as off.</summary>
    private DailyIncomingPaymentSettings? postingSwitch;

    private readonly HashSet<DateTime> openDays = [];

    private DailyIncomingPaymentSummary? selected;
    private DailyIncomingPaymentDetail? detail;

    private List<NocturnePickerOption> accountOptions = [];
    private Dictionary<string, string> accountNames = new(StringComparer.OrdinalIgnoreCase);
    private bool accountsLoading = true;

    private MappingForm? editing;
    private bool isNew;
    private bool codeLocked;

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

    private string PaymentsLabel
    {
        get
        {
            var shown = Visible.Count;
            return shown == payments.Count
                ? Plural(shown, "payment")
                : $"{shown} of {Plural(payments.Count, "payment")}";
        }
    }

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
        await Task.WhenAll(LoadPaymentsCoreAsync(), LoadMappingsAsync(), LoadSwitchAsync());
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
        selected = null;
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

        // The newest day opens; the rest wait folded, each still showing its totals.
        openDays.Clear();
        if (payments.Count > 0)
        {
            openDays.Add(payments.Max(payment => payment.PaymentDate.Date));
        }
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
        mappingsByCode = mappings.ToDictionary(mapping => mapping.CardCode, StringComparer.OrdinalIgnoreCase);
    }

    private async Task LoadSwitchAsync() =>
        postingSwitch = await PaymentSettings.GetAsync();

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

    private void ToggleDay(DateTime day)
    {
        if (!openDays.Remove(day))
        {
            openDays.Add(day);
        }
    }

    // ---- One payment --------------------------------------------------------------------------------

    private async Task OpenAsync(DailyIncomingPaymentSummary payment)
    {
        error = null;
        selected = payment;
        detail = null;
        openDays.Add(payment.PaymentDate.Date);

        var result = await Mediator.Send(new GetDailyIncomingPaymentQuery(payment.Id));
        if (selected?.Id != payment.Id)
        {
            return;
        }

        if (result.IsError)
        {
            error = result.FirstError.Description;
            selected = null;
            return;
        }

        detail = result.Value;
    }

    private void ClosePayment()
    {
        selected = null;
        detail = null;
    }

    // ---- Editing a mapping ------------------------------------------------------------------------

    private void StartNew()
    {
        error = null;
        isNew = true;
        codeLocked = false;
        editing = new MappingForm { Run = ShopsRun, IsActive = true };
    }

    private void StartEdit(IncomingPaymentGlMapping mapping)
    {
        error = null;
        isNew = false;
        codeLocked = true;
        editing = new MappingForm
        {
            CardCode = mapping.CardCode,
            CardName = mapping.CardName,
            CashAccount = mapping.CashAccount,
            ElectronicAccount = mapping.ElectronicAccount,
            SameAccount = string.Equals(mapping.CashAccount, mapping.ElectronicAccount, StringComparison.OrdinalIgnoreCase),
            Run = mapping.Run,
            Emails = Emails(mapping.NotifyEmails).ToList(),
            IsActive = mapping.IsActive
        };
    }

    /// <summary>
    /// From a held payment: the partner's row if it has one (held because it is inactive), else a new row
    /// with the code already filled in.
    /// </summary>
    private void StartMapping(string cardCode, string? cardName)
    {
        ClosePayment();
        tab = Tab.Accounts;

        if (MappingFor(cardCode) is { } mapping)
        {
            StartEdit(mapping);
            return;
        }

        StartNew();
        codeLocked = true;
        editing!.CardCode = cardCode;
        editing.CardName = cardName;
    }

    private void SetRun(string run)
    {
        if (editing is null)
        {
            return;
        }

        // Vans post everything to one account, so choosing the van run ticks "same"; it can still be unticked.
        if (run == VansRun && editing.Run != VansRun && string.IsNullOrWhiteSpace(editing.ElectronicAccount))
        {
            editing.SameAccount = true;
        }

        editing.Run = run;
    }

    private void SplitEmailDraft()
    {
        if (editing?.EmailDraft is { } draft && draft.IndexOfAny([',', ';', ' ']) >= 0)
        {
            CommitEmailDraft();
        }
    }

    private void CommitEmailDraft()
    {
        if (editing is null)
        {
            return;
        }

        foreach (var address in Emails(editing.EmailDraft))
        {
            if (!editing.Emails.Contains(address, StringComparer.OrdinalIgnoreCase))
            {
                editing.Emails.Add(address);
            }
        }

        editing.EmailDraft = null;
    }

    private void OnEmailKeyDown(KeyboardEventArgs args)
    {
        if (editing is null)
        {
            return;
        }

        if (args.Key == "Enter")
        {
            CommitEmailDraft();
        }
        else if (args.Key == "Backspace" && string.IsNullOrEmpty(editing.EmailDraft) && editing.Emails.Count > 0)
        {
            editing.Emails.RemoveAt(editing.Emails.Count - 1);
        }
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

        CommitEmailDraft();

        var electronic = editing.SameAccount ? editing.CashAccount : editing.ElectronicAccount;
        if (string.IsNullOrWhiteSpace(editing.CardCode)
            || string.IsNullOrWhiteSpace(editing.CashAccount)
            || string.IsNullOrWhiteSpace(electronic))
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
                    electronic,
                    editing.Run,
                    editing.Emails.Count == 0 ? null : string.Join(", ", editing.Emails),
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

    // ---- Shaping the lists ----------------------------------------------------------------------------

    private IncomingPaymentGlMapping? MappingFor(string cardCode) =>
        mappingsByCode.GetValueOrDefault(cardCode);

    /// <summary>
    /// The run that pays a partner. The API's run scope puts van-mapped partners in the van run and every
    /// other partner, an unmapped one included, in the shop run.
    /// </summary>
    private string RunOf(DailyIncomingPaymentSummary payment) =>
        MappingFor(payment.CardCode)?.Run == VansRun ? VansRun : ShopsRun;

    private List<DailyIncomingPaymentSummary> Visible =>
        runFilter is null ? payments : payments.Where(payment => RunOf(payment) == runFilter).ToList();

    private IEnumerable<DayGroup> Days(IReadOnlyList<DailyIncomingPaymentSummary> source) =>
        source
            .GroupBy(payment => payment.PaymentDate.Date)
            .OrderByDescending(day => day.Key)
            .Select(day => new DayGroup(
                day.Key,
                RunOrder
                    .Select(run => day.Where(payment => RunOf(payment) == run)
                        .OrderBy(payment => payment.CardCode, StringComparer.OrdinalIgnoreCase)
                        .ToList())
                    .Where(list => list.Count > 0)
                    .Select(list => new RunGroup(
                        RunOf(list[0]),
                        list,
                        list.Count(payment => payment.Status == "Posted"),
                        list.Sum(payment => payment.Total)))
                    .ToList(),
                day.Count(),
                day.Count(IsNotPosted),
                day.Sum(payment => payment.InvoiceCount),
                day.Sum(payment => payment.Total)));

    /// <summary>What a person has to act on: a payment not in SAP, or one in SAP whose cash holders were not told.</summary>
    private List<DailyIncomingPaymentSummary> Attention =>
        Visible
            .Where(payment => IsNotPosted(payment) || (payment.Status == "Posted" && payment.EmailError is not null))
            .OrderByDescending(payment => payment.PaymentDate)
            .ThenBy(payment => payment.CardCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsNotPosted(DailyIncomingPaymentSummary payment) =>
        payment.Status is "Pending" or "Unresolved";

    private static bool IsHeldForMapping(DailyIncomingPaymentSummary payment) =>
        payment.Status == "Pending"
        && payment.LastError?.StartsWith(HeldForMappingPrefix, StringComparison.Ordinal) == true;

    private List<DailyIncomingPaymentSummary> HeldFor(string? cardCode) =>
        string.IsNullOrWhiteSpace(cardCode)
            ? []
            : payments
                .Where(payment => IsHeldForMapping(payment)
                                  && string.Equals(payment.CardCode, cardCode.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(payment => payment.PaymentDate)
                .ToList();

    /// <summary>
    /// Partners whose payments are held for want of accounts and still have none. A partner mapped since
    /// drops out at once, though its payment keeps the held status until the next pass posts it.
    /// </summary>
    private List<HeldPartner> HeldPartners =>
        payments
            .Where(payment => IsHeldForMapping(payment) && MappingFor(payment.CardCode) is not { IsActive: true })
            .GroupBy(payment => payment.CardCode, StringComparer.OrdinalIgnoreCase)
            .Select(partner => new HeldPartner(
                partner.Key,
                partner.Select(payment => payment.CardName).FirstOrDefault(name => name is not null),
                partner.Sum(payment => payment.InvoiceCount),
                partner.Sum(payment => payment.Total),
                partner.Min(payment => payment.PaymentDate)))
            .OrderBy(partner => partner.CardCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Each account an active partner posts to, and which partners send it cash or electronic money.</summary>
    private List<AccountUse> AccountUses
    {
        get
        {
            var active = mappings.Where(mapping => mapping.IsActive).ToList();
            return active
                .SelectMany(mapping => new[] { mapping.CashAccount, mapping.ElectronicAccount })
                .Where(account => !string.IsNullOrWhiteSpace(account))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(account => new AccountUse(
                    account,
                    active.Where(mapping => string.Equals(mapping.CashAccount, account, StringComparison.OrdinalIgnoreCase))
                        .Select(mapping => mapping.CardCode).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                    active.Where(mapping => string.Equals(mapping.ElectronicAccount, account, StringComparison.OrdinalIgnoreCase)
                                            && !string.Equals(mapping.CashAccount, account, StringComparison.OrdinalIgnoreCase))
                        .Select(mapping => mapping.CardCode).Order(StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();
        }
    }

    private IEnumerable<MappingGroup> MappingGroups =>
        mappings
            .GroupBy(mapping => mapping.Run)
            .OrderBy(group => Array.IndexOf(RunOrder, group.Key) is var index and >= 0 ? index : RunOrder.Length)
            .Select(group => new MappingGroup(
                group.Key,
                group.OrderBy(mapping => mapping.CardCode, StringComparer.OrdinalIgnoreCase).ToList()));

    // ---- Run status -----------------------------------------------------------------------------------

    private string PostingLabel => postingSwitch switch
    {
        null => "Unknown",
        { Enabled: true } => "On",
        _ => "Paused"
    };

    private string PostingDot => postingSwitch switch
    {
        null => "is-neutral",
        { Enabled: true } => "is-good",
        _ => "is-warn"
    };

    private string PostingMeta => postingSwitch switch
    {
        null => "Couldn't read the switch.",
        { UpdatedAtUtc: { } changed, Enabled: true } => $"Turned on {ToCat(changed, "dd MMM HH:mm")}.",
        { UpdatedAtUtc: { } changed } => $"Paused since {ToCat(changed, "dd MMM HH:mm")}.",
        _ => "Using the server default."
    };

    private string NextRun(string run)
    {
        if (postingSwitch is { Enabled: false })
        {
            return "Paused";
        }

        var time = run == VansRun ? VansRunTime : ShopsRunTime;
        var now = IAuditService.ToCAT(DateTime.UtcNow);
        return $"Next {(now.TimeOfDay < time ? "today" : "tomorrow")} {time:hh\\:mm}";
    }

    /// <summary>When the run last posted in the loaded range, and how many it posted that day.</summary>
    private (DateTime PostedAtUtc, int Count)? LastPosted(string run)
    {
        var posted = payments
            .Where(payment => payment.PostedAtUtc is not null && RunOf(payment) == run)
            .ToList();
        if (posted.Count == 0)
        {
            return null;
        }

        var last = posted.MaxBy(payment => payment.PostedAtUtc)!;
        return (last.PostedAtUtc!.Value, posted.Count(payment => payment.PaymentDate.Date == last.PaymentDate.Date));
    }

    // ---- Display --------------------------------------------------------------------------------------

    private string AccountName(string? code) =>
        code is not null && accountNames.TryGetValue(code, out var name) ? name : string.Empty;

    /// <summary>The accounts the payment posted to, or for one not yet posted, the partner's accounts today.</summary>
    private static string AccountsCell(DailyIncomingPaymentSummary payment, IncomingPaymentGlMapping? mapping)
    {
        var cash = payment.CashAccount ?? mapping?.CashAccount;
        var electronic = payment.TransferAccount ?? mapping?.ElectronicAccount;

        if (cash is null && electronic is null)
        {
            return "not mapped";
        }

        return string.Equals(cash, electronic, StringComparison.OrdinalIgnoreCase)
            ? $"{cash} · same"
            : $"{cash ?? "—"} · {electronic ?? "—"}";
    }

    private static string RunLabel(string run) => run == VansRun
        ? $"Vans · {VansRunTime:hh\\:mm}"
        : $"Shops · {ShopsRunTime:hh\\:mm}";

    private static string StatusLabel(DailyIncomingPaymentSummary payment) => payment.Status switch
    {
        "Pending" when IsHeldForMapping(payment) => "Held: no G/L",
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

    private static string AttentionTone(DailyIncomingPaymentSummary payment) => payment.Status switch
    {
        "Posted" => "info",
        "Unresolved" => "bad",
        _ => "warn"
    };

    private static string AttentionTitle(DailyIncomingPaymentSummary payment) =>
        payment.Status == "Posted" ? "Email failed" : StatusLabel(payment);

    private static string AttentionReason(DailyIncomingPaymentSummary payment) => payment.Status switch
    {
        "Posted" => $"posted as {payment.SapDocNum}, cash holders not told",
        "Unresolved" => "sent, and SAP's answer never arrived",
        "Pending" when IsHeldForMapping(payment) => "no active G/L mapping",
        "Pending" when payment.LastError is not null => "SAP refused the last attempt",
        _ => "waiting for the next pass"
    };

    /// <summary>What the drawer says is wrong with a payment, in the words of the status the API gave it.</summary>
    private static Problem? ProblemFor(DailyIncomingPaymentSummary payment) => payment.Status switch
    {
        "Unresolved" => new Problem("bad", "Sent, and SAP's answer never arrived",
            "Nothing is sent again until SAP has been asked whether it holds this payment. " + payment.LastError),
        "Pending" when IsHeldForMapping(payment) => new Problem("warn", $"Held: {payment.CardCode} has no active G/L accounts",
            "Nothing was sent to SAP, so nothing landed in the default account. " + payment.LastError),
        "Pending" when payment.LastError is not null => new Problem("warn", "SAP refused the last attempt", payment.LastError),
        "Pending" => new Problem("warn", "Claimed, not sent yet", "Its invoices are held for this payment; the next pass sends it."),
        "Released" => new Problem("neutral", "Released", payment.LastError),
        "NothingToPay" => new Problem("neutral", "Nothing to pay",
            "Nothing was left to pay once SAP's balances were read, so nothing was sent."),
        "Posted" when payment.EmailError is not null => new Problem("info", "Posted, but the email failed", payment.EmailError),
        _ => null
    };

    private static string EmailState(DailyIncomingPaymentSummary payment) => payment switch
    {
        { EmailSentAtUtc: { } sent } => $"Sent {ToCat(sent, "dd MMM HH:mm")}",
        { EmailError: not null } => "Failed",
        { Status: "Posted" } => "Not sent yet",
        _ => "Sent once the payment posts"
    };

    private static string EmailDot(DailyIncomingPaymentSummary payment) => payment switch
    {
        { EmailSentAtUtc: not null } => "is-good",
        { EmailError: not null } => "is-bad",
        _ => "is-neutral"
    };

    private static string LineKey(DailyIncomingPaymentLine line) => (line.CashAmount != 0, line.ElectronicAmount != 0) switch
    {
        (true, false) => "dpy-key-cash",
        (false, true) => "dpy-key-el",
        _ => "dpy-key-mixed"
    };

    private static IReadOnlyList<string> Emails(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int Share(decimal part, decimal whole) =>
        whole <= 0 ? 0 : (int)Math.Round(part / whole * 100, MidpointRounding.AwayFromZero);

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", Invariant)} {noun}s";

    private static string Day(DateTime date) => date.ToString("ddd dd MMM", Invariant);

    private static string Money(decimal value) => value.ToString("N2", Invariant);

    private static string ToCat(DateTime utc, string format = "dd MMM yyyy HH:mm") => IAuditService.ToCAT(
            utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc))
        .ToString(format, Invariant);

    private static DateTime Today() =>
        IAuditService.ToCAT(DateTime.UtcNow).Date;

    private sealed record DayGroup(DateTime Date, IReadOnlyList<RunGroup> Runs, int Count, int NotPosted, int Invoices, decimal Total);

    private sealed record RunGroup(string Run, IReadOnlyList<DailyIncomingPaymentSummary> Payments, int Posted, decimal Total);

    private sealed record HeldPartner(string CardCode, string? CardName, int Invoices, decimal Total, DateTime Since);

    private sealed record AccountUse(string Account, IReadOnlyList<string> Cash, IReadOnlyList<string> Electronic);

    private sealed record MappingGroup(string Run, IReadOnlyList<IncomingPaymentGlMapping> Mappings);

    private sealed record Problem(string Tone, string Title, string? Text);

    /// <summary>The drawer's working copy.</summary>
    private sealed class MappingForm
    {
        public string CardCode { get; set; } = string.Empty;
        public string? CardName { get; set; }
        public string? CashAccount { get; set; }
        public string? ElectronicAccount { get; set; }
        public bool SameAccount { get; set; }
        public string Run { get; set; } = ShopsRun;
        public List<string> Emails { get; set; } = [];
        public string? EmailDraft { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
