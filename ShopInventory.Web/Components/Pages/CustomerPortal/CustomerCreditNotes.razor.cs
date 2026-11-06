using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShopInventory.Web.Features.CustomerPortal.Queries.DownloadPortalCreditNotePdf;
using ShopInventory.Web.Features.CustomerPortal.Queries.GetPortalCreditNotes;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages.CustomerPortal;

/// <summary>
/// The customer's credit notes for a period: what each credited, how much of it is used, and its PDF.
/// </summary>
public partial class CustomerCreditNotes
{
    private const int PageSize = 15;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ICustomerPortalSessionService CustomerSession { get; set; } = default!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] private ILogger<CustomerCreditNotes> Logger { get; set; } = default!;

    private readonly CancellationTokenSource _disposed = new();

    private CustomerPortalSession? portalSession;
    private CustomerInfo? customerInfo;
    private bool isMultiAccount;
    private List<LinkedAccountInfo> linkedAccounts = new();
    private string selectedAccountFilter = string.Empty;

    // Blank on arrival, as on the invoices page: this is a live read of the customer's ledger, and the
    // reader has not said which period they want yet.
    private DateTime? fromDate;
    private DateTime? toDate;
    private string findText = string.Empty;
    private CreditNoteShow show = CreditNoteShow.All;

    private List<CustomerCreditNote> creditNotes = new();
    private bool isLoading = true;
    private bool hasLoaded;
    private string? loadError;
    private DateTime? lastUpdated;
    private int currentPage = 1;

    private CustomerCreditNote? selectedNote;
    private string? sheetError;
    private int? downloadingDocEntry;

    private enum CreditNoteShow
    {
        All,
        CreditLeft,
        Applied
    }

    private static readonly (CreditNoteShow Show, string Label)[] ShowOptions =
    [
        (CreditNoteShow.All, "All"),
        (CreditNoteShow.CreditLeft, "Credit left"),
        (CreditNoteShow.Applied, "Applied")
    ];

    private bool HasPeriod => fromDate.HasValue && toDate.HasValue;

    // Refused at the button rather than sent: neither is a query the API will answer.
    private bool PeriodIsInverted => HasPeriod && fromDate > toDate;

    private bool PeriodIsTooLong => HasPeriod && !PeriodIsInverted
        && (toDate!.Value.Date - fromDate!.Value.Date).TotalDays > GetPortalCreditNotesValidator.MaxRangeDays;

    private IEnumerable<NocturneSelectOption<string>> AccountFilterOptions =>
        PortalFilterOptions.Accounts(linkedAccounts);

    /// <summary>The loaded notes on the account the filter names, or on all of them.</summary>
    private List<CustomerCreditNote> AccountNotes => string.IsNullOrEmpty(selectedAccountFilter)
        ? creditNotes
        : creditNotes.Where(note => string.Equals(note.CardCode, selectedAccountFilter, StringComparison.OrdinalIgnoreCase)).ToList();

    private List<CustomerCreditNote> NotesWithCreditLeft => AccountNotes.Where(note => note.CreditLeft > 0).ToList();

    private CustomerCreditNote? LatestNote => AccountNotes
        .OrderByDescending(note => note.DocDate)
        .ThenByDescending(note => note.DocNum)
        .FirstOrDefault();

    /// <summary>The ledger's rows: the account, then the segment, then the number typed into Find.</summary>
    private List<CustomerCreditNote> ShownNotes
    {
        get
        {
            var find = findText.Trim();
            return AccountNotes
                .Where(note => InShow(note, show))
                .Where(note => find.Length == 0
                    || note.DocNum.ToString(CultureInfo.InvariantCulture).Contains(find, StringComparison.OrdinalIgnoreCase)
                    || (note.CreditedInvoiceDocNum > 0
                        && note.CreditedInvoiceDocNum.ToString(CultureInfo.InvariantCulture).Contains(find, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    private int totalPages => (int)Math.Ceiling(ShownNotes.Count / (double)PageSize);

    private List<CustomerCreditNote> pagedNotes => ShownNotes
        .Skip((currentPage - 1) * PageSize)
        .Take(PageSize)
        .ToList();

    protected override async Task OnInitializedAsync()
    {
        // The session lives behind JS interop; see CustomerPortalLayout for why the guard is kept.
        if (!RendererInfo.IsInteractive)
        {
            return;
        }

        try
        {
            portalSession = await CustomerSession.GetCurrentSessionAsync();
            customerInfo = portalSession?.Customer;

            if (customerInfo is not null)
            {
                isMultiAccount = customerInfo.AccountStructure == "Multi";
                linkedAccounts = portalSession?.LinkedAccounts.ToList() ?? new List<LinkedAccountInfo>();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading the portal session for the credit notes page");
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task LoadCreditNotes()
    {
        if (customerInfo is null || !HasPeriod || PeriodIsInverted || PeriodIsTooLong)
        {
            return;
        }

        isLoading = true;
        loadError = null;

        try
        {
            var result = await Mediator.Send(
                new GetPortalCreditNotesQuery(customerInfo.CardCode, fromDate!.Value, toDate!.Value),
                _disposed.Token);

            if (result.IsError)
            {
                loadError = result.FirstError.Description;
                return;
            }

            creditNotes = result.Value;
            lastUpdated = DateTime.UtcNow;
            hasLoaded = true;
            currentPage = 1;
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading credit notes for {CardCode}", customerInfo.CardCode);
            loadError = "We could not load your credit notes just now. Please try again.";
        }
        finally
        {
            isLoading = false;
        }
    }

    private void OnAccountFilterChanged(string value)
    {
        if (!CustomerSession.CanAccessCardCode(portalSession, value))
        {
            Logger.LogWarning("Rejected unauthorized customer credit note account filter {CardCode}", value);
            selectedAccountFilter = string.Empty;
        }
        else
        {
            selectedAccountFilter = value;
        }

        currentPage = 1;
    }

    private void SetShow(CreditNoteShow value)
    {
        show = value;
        currentPage = 1;
    }

    private void ShowCreditLeft()
    {
        findText = string.Empty;
        SetShow(CreditNoteShow.CreditLeft);
    }

    private void ResetPage() => currentPage = 1;

    private void GoToPage(int page)
    {
        if (page >= 1 && page <= totalPages)
        {
            currentPage = page;
        }
    }

    private void OpenSheet(CustomerCreditNote note)
    {
        selectedNote = note;
        sheetError = null;
    }

    private void CloseSheet()
    {
        selectedNote = null;
        sheetError = null;
    }

    private async Task DownloadPdf(CustomerCreditNote note)
    {
        if (customerInfo is null || downloadingDocEntry.HasValue)
        {
            return;
        }

        downloadingDocEntry = note.DocEntry;
        sheetError = null;
        loadError = null;

        try
        {
            var result = await Mediator.Send(
                new DownloadPortalCreditNotePdfQuery(customerInfo.CardCode, note.DocEntry, note.CardCode),
                _disposed.Token);

            if (result.IsError)
            {
                ShowDownloadError(result.FirstError.Description);
                return;
            }

            await JSRuntime.InvokeVoidAsync(
                "downloadPdfFromBase64",
                Convert.ToBase64String(result.Value),
                $"CreditNote_{note.DocNum}.pdf");
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error downloading the PDF of credit note {DocEntry}", note.DocEntry);
            ShowDownloadError("We could not download that credit note just now. Please try again.");
        }
        finally
        {
            downloadingDocEntry = null;
        }
    }

    /// <summary>In the sheet when it is open — that is where the button was — and above the list when not.</summary>
    private void ShowDownloadError(string message)
    {
        if (selectedNote is not null)
        {
            sheetError = message;
        }
        else
        {
            loadError = message;
        }
    }

    private static bool InShow(CustomerCreditNote note, CreditNoteShow value) => value switch
    {
        CreditNoteShow.CreditLeft => note.CreditLeft > 0,
        CreditNoteShow.Applied => note.CreditLeft <= 0,
        _ => true
    };

    private int CountFor(CreditNoteShow value) => AccountNotes.Count(note => InShow(note, value));

    /// <summary>Applied, part applied or not applied — said the same way in the list and the sheet.</summary>
    private static (string Cls, string Text) Status(CustomerCreditNote note)
    {
        if (note.CreditLeft <= 0) return ("is-paid", "Applied");
        if (note.AppliedAmount <= 0) return ("is-open", "Not applied");
        return (string.Empty, "Part applied");
    }

    private string AppliedCaption()
    {
        var applied = AccountNotes.Count(note => note.CreditLeft <= 0);
        var part = AccountNotes.Count(note => note.CreditLeft > 0 && note.AppliedAmount > 0);

        if (AccountNotes.Count == 0) return "None in this period";
        if (part == 0) return $"{applied} fully applied";
        return $"{applied} fully applied, {part} in part";
    }

    private string EmptyLedgerText()
    {
        if (AccountNotes.Count == 0) return "No credit notes were issued in this period.";
        if (findText.Trim().Length > 0) return "No credit notes match that number.";
        return show == CreditNoteShow.CreditLeft
            ? "Every credit note in this period has been applied."
            : "No credit notes have been applied in this period yet.";
    }

    private string PeriodCaption() => fromDate.HasValue && toDate.HasValue
        ? $"{fromDate:MMM dd} – {toDate:MMM dd, yyyy}"
        : "This period";

    private string AccountScopeCaption()
    {
        if (string.IsNullOrEmpty(selectedAccountFilter))
        {
            return "All accounts";
        }

        return linkedAccounts.FirstOrDefault(account => account.CardCode == selectedAccountFilter)?.CardName
            ?? selectedAccountFilter;
    }

    private static string LineSubCaption(CustomerCreditNoteLine line)
    {
        var parts = new[] { line.ItemCode, line.Reason }.Where(part => !string.IsNullOrWhiteSpace(part));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// A sum per currency. A multi-account customer can hold a USD and a ZiG account, and one figure
    /// added across the two would be neither.
    /// </summary>
    private static string FormatTotals(IEnumerable<CustomerCreditNote> notes, Func<CustomerCreditNote, decimal> amount)
    {
        var byCurrency = notes
            .GroupBy(note => note.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Currency: group.Key, Sum: group.Sum(amount)))
            .OrderByDescending(entry => IsUsd(entry.Currency))
            .ThenBy(entry => entry.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (byCurrency.Count == 0) return FormatMoney(0m, "USD");
        if (byCurrency.Count == 1) return FormatMoney(byCurrency[0].Sum, byCurrency[0].Currency);

        return string.Join(" + ", byCurrency.Select(entry => FormatMoneyWithCode(entry.Sum, entry.Currency)));
    }

    /// <summary>The portal's "$1,234.50" for USD, the currency's own code for anything else.</summary>
    private static string FormatMoney(decimal amount, string? currency) =>
        IsUsd(currency)
            ? "$" + amount.ToString("N2", CultureInfo.InvariantCulture)
            : FormatMoneyWithCode(amount, currency);

    private static string FormatMoneyWithCode(decimal amount, string? currency) =>
        $"{(string.IsNullOrWhiteSpace(currency) ? "USD" : currency)} {amount.ToString("N2", CultureInfo.InvariantCulture)}";

    private static bool IsUsd(string? currency) =>
        string.IsNullOrWhiteSpace(currency) || string.Equals(currency.Trim(), "USD", StringComparison.OrdinalIgnoreCase);

    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
    }
}
