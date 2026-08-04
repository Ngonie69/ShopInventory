using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class Customers : ComponentBase
{
    private const string FilterAll = "all";
    private const string FilterNone = "__none__";
    private const string ScopeAll = "";
    private const string ScopeCustomer = "cCustomer";
    private const string ScopeSupplier = "cSupplier";

    private static readonly int[] PageSizes = [10, 25, 50, 100, 200];

    private static readonly (string Value, string Label)[] Scopes =
    [
        (ScopeCustomer, "Customers"),
        (ScopeSupplier, "Suppliers"),
        (ScopeAll, "All")
    ];

    [Inject] private IBusinessPartnerService BusinessPartnerService { get; set; } = null!;
    [Inject] private HttpClient Http { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private IAppSettingsProvider AppSettings { get; set; } = null!;
    [Inject] private IAuditService AuditService { get; set; } = null!;

    private readonly StringComparer _textComparer = StringComparer.OrdinalIgnoreCase;

    private List<BusinessPartnerDto> customers = [];
    private BusinessPartnerDto? selectedCustomer;
    private bool isLoading = true;
    private bool isSyncing;
    private bool isGeneratingStatement;
    private bool isExporting;
    private bool showFilters;
    private string? errorMessage;
    private string scope = ScopeAll;
    private string channelFilter = FilterAll;
    private string currencyFilter = FilterAll;
    private DateTime? statementFromDate = DateTime.Today.AddMonths(-3);
    private DateTime? statementToDate = DateTime.Today;
    private DateTime? lastSyncTime;
    private int pageSize = 25;
    private int pageNumber = 1;

    // One search, filtered in memory. The cache query behind this page returns
    // every partner in a single call and the old server-side search matched the
    // same fields off the same table, so a round trip per keystroke bought
    // nothing the client cannot do instantly.
    private string _searchTerm = string.Empty;
    private string searchTerm
    {
        get => _searchTerm;
        set { _searchTerm = value; pageNumber = 1; }
    }

    private List<BusinessPartnerDto> Filtered =>
        customers
            .Where(MatchesScope)
            .Where(MatchesSearch)
            .Where(MatchesChannel)
            .Where(MatchesCurrency)
            .ToList();

    private int AllCount => customers.Count;
    private int CustomerCount => customers.Count(c => IsCustomer(c.CardType));
    private int SupplierCount => customers.Count(c => IsSupplier(c.CardType));
    private int ActiveCount => customers.Count(c => c.IsActive);
    private decimal TotalOutstanding => customers.Where(c => c.Balance > 0).Sum(c => c.Balance ?? 0m);

    private int ActiveFilterCount =>
        (channelFilter == FilterAll ? 0 : 1) + (currencyFilter == FilterAll ? 0 : 1);

    private List<string> AvailableChannels =>
        customers
            .Select(c => c.Channel?.Trim())
            .Where(channel => !string.IsNullOrWhiteSpace(channel))
            .Select(channel => channel!)
            .Distinct(_textComparer)
            .OrderBy(channel => channel, _textComparer)
            .ToList();

    private bool HasPartnersWithoutChannel => customers.Any(c => string.IsNullOrWhiteSpace(c.Channel));

    private List<string> AvailableCurrencies =>
        customers
            .Select(c => c.Currency?.Trim())
            .Where(currency => !string.IsNullOrWhiteSpace(currency))
            .Select(currency => currency!)
            .Distinct(_textComparer)
            .OrderBy(currency => currency, _textComparer)
            .ToList();

    private static readonly NocturneSelectOption<int>[] PageSizeSelectOptions =
        PageSizes.Select(size => new NocturneSelectOption<int>(size, size.ToString())).ToArray();

    // IsUnset on the "All" rows because this page's no-filter sentinel is the
    // word "all", not an empty string — without it the trigger would sit in its
    // accent "a filter is set" state from the moment the page loaded.
    private IEnumerable<NocturneSelectOption<string>> ChannelFilterOptions
    {
        get
        {
            var options = new List<NocturneSelectOption<string>>
            {
                new(FilterAll, "All channels", "neutral") { IsUnset = true, RuleAfter = !HasPartnersWithoutChannel }
            };

            if (HasPartnersWithoutChannel)
            {
                options.Add(new NocturneSelectOption<string>(FilterNone, "No channel", "neutral") { RuleAfter = true });
            }

            options.AddRange(AvailableChannels.Select(channel =>
                new NocturneSelectOption<string>(channel, channel, "info")));

            return options;
        }
    }

    private IEnumerable<NocturneSelectOption<string>> CurrencyFilterOptions =>
        AvailableCurrencies
            .Select(currency => new NocturneSelectOption<string>(currency, currency, "accent"))
            .Prepend(new NocturneSelectOption<string>(FilterAll, "All currencies", "neutral")
            {
                IsUnset = true,
                RuleAfter = true
            });

    protected override async Task OnInitializedAsync()
    {
        pageSize = PageSizes.Contains(AppSettings.PageSize) ? AppSettings.PageSize : 25;
        await LoadCustomers();
        lastSyncTime = await BusinessPartnerService.GetLastSyncTimeAsync();
        await AuditService.LogAsync(AuditActions.ViewCustomers, "Customer", null);
    }

    private async Task LoadCustomers()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            var response = await BusinessPartnerService.GetCachedBusinessPartnersAsync();

            if (response is null)
            {
                customers = [];
                errorMessage = "No cached data found. Refresh to sync from the server.";
            }
            else
            {
                customers = response.BusinessPartners ?? [];
                DropFiltersWithNothingLeftToMatch();
            }
        }
        catch (Exception ex)
        {
            errorMessage = ApiErrorResponse.GetFriendlyMessage(
                ex,
                "We couldn't load the business partners right now. Please try again.");
        }
        finally
        {
            isLoading = false;
            pageNumber = 1;
        }
    }

    // A sync can retire the only partner on a channel or in a currency. Leaving
    // the filter set would show an empty directory with no obvious cause.
    private void DropFiltersWithNothingLeftToMatch()
    {
        if (channelFilter == FilterNone && !HasPartnersWithoutChannel)
            channelFilter = FilterAll;

        if (channelFilter != FilterAll && channelFilter != FilterNone &&
            !AvailableChannels.Contains(channelFilter, _textComparer))
            channelFilter = FilterAll;

        if (currencyFilter != FilterAll && !AvailableCurrencies.Contains(currencyFilter, _textComparer))
            currencyFilter = FilterAll;
    }

    private async Task RefreshFromApi()
    {
        isSyncing = true;
        errorMessage = null;
        StateHasChanged();

        try
        {
            await BusinessPartnerService.SyncBusinessPartnersAsync();
            lastSyncTime = DateTime.UtcNow;
            await LoadCustomers();
        }
        catch (Exception ex)
        {
            errorMessage = ApiErrorResponse.GetFriendlyMessage(
                ex,
                "We couldn't sync the business partners right now. Please try again.");
        }
        finally
        {
            isSyncing = false;
        }
    }

    private void SetScope(string value)
    {
        scope = value;
        pageNumber = 1;
    }

    private void ResetPage() => pageNumber = 1;

    private void ClearFilters()
    {
        channelFilter = FilterAll;
        currencyFilter = FilterAll;
        pageNumber = 1;
    }

    private void ClearAllCriteria()
    {
        _searchTerm = string.Empty;
        scope = ScopeAll;
        ClearFilters();
    }

    private bool MatchesScope(BusinessPartnerDto partner) => scope switch
    {
        ScopeCustomer => IsCustomer(partner.CardType),
        ScopeSupplier => IsSupplier(partner.CardType),
        _ => true
    };

    private bool MatchesSearch(BusinessPartnerDto partner)
    {
        var term = _searchTerm.Trim();
        if (term.Length == 0)
            return true;

        return Contains(partner.CardCode, term)
            || Contains(partner.CardName, term)
            || Contains(partner.Email, term)
            || Contains(partner.Phone1, term)
            || Contains(partner.City, term)
            || Contains(partner.Country, term);
    }

    private bool MatchesChannel(BusinessPartnerDto partner) => channelFilter switch
    {
        FilterAll => true,
        FilterNone => string.IsNullOrWhiteSpace(partner.Channel),
        _ => string.Equals(partner.Channel?.Trim(), channelFilter, StringComparison.OrdinalIgnoreCase)
    };

    private bool MatchesCurrency(BusinessPartnerDto partner) => currencyFilter == FilterAll
        || string.Equals(partner.Currency?.Trim(), currencyFilter, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? value, string term)
        => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    // ── Paging ──────────────────────────────────────────────────────────────

    private int PageCount(int rowCount) => Math.Max(1, (rowCount + pageSize - 1) / pageSize);

    // Filtering can shrink the directory under the page currently being read.
    private int ClampPage(int rowCount)
    {
        var pages = PageCount(rowCount);
        if (pageNumber > pages)
            pageNumber = pages;
        if (pageNumber < 1)
            pageNumber = 1;
        return pageNumber;
    }

    private void GoToPage(int page) => pageNumber = page;

    // The design's "1 2 3 … 101": the ends, the current page and its
    // neighbours, with a gap standing in for everything skipped.
    private static List<int?> PageSlots(int current, int pages)
    {
        var slots = new List<int?>();
        var last = 0;

        for (var page = 1; page <= pages; page++)
        {
            var keep = page == 1 || page == pages || Math.Abs(page - current) <= 1;
            if (!keep)
                continue;

            if (last > 0 && page - last > 1)
                slots.Add(null);

            slots.Add(page);
            last = page;
        }

        return slots;
    }

    // ── Drawer ──────────────────────────────────────────────────────────────

    private void ViewCustomer(BusinessPartnerDto partner) => selectedCustomer = partner;

    private void CloseDrawer() => selectedCustomer = null;

    private void NavigateToInvoices(string cardCode)
    {
        selectedCustomer = null;
        NavigationManager.NavigateTo($"/invoices?customer={cardCode}");
    }

    private async Task GenerateStatement(string cardCode)
    {
        isGeneratingStatement = true;
        try
        {
            var fromDate = (statementFromDate ?? DateTime.Today.AddMonths(-3)).ToString("yyyy-MM-dd");
            var toDate = (statementToDate ?? DateTime.Today).ToString("yyyy-MM-dd");
            var response = await Http.GetAsync(
                $"api/statement/generate/{cardCode}?fromDate={fromDate}&toDate={toDate}");

            if (response.IsSuccessStatusCode)
            {
                var fileBytes = await response.Content.ReadAsByteArrayAsync();
                var fileName = $"Statement_{cardCode}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";

                await JS.InvokeVoidAsync("downloadFile", fileName, Convert.ToBase64String(fileBytes));
                await AuditService.LogAsync(AuditActions.GenerateStatement, "Customer", cardCode);
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                errorMessage = ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode,
                    errorContent,
                    "We couldn't generate this statement right now. Please try again.");
            }
        }
        catch (Exception ex)
        {
            errorMessage = ApiErrorResponse.GetFriendlyMessage(
                ex,
                "We couldn't generate this statement right now. Please try again.");
        }
        finally
        {
            isGeneratingStatement = false;
        }
    }

    // ── Export ──────────────────────────────────────────────────────────────

    // The directory as it currently reads — the search, the scope and the
    // filters all applied, every page of it, not just the one on screen.
    private async Task ExportCsv(List<BusinessPartnerDto> rows)
    {
        isExporting = true;
        try
        {
            var csv = new StringBuilder();
            csv.AppendLine("Code,Name,Type,Email,Phone,City,Country,Currency,Balance,Status");

            foreach (var partner in rows)
            {
                csv.Append(Csv(partner.CardCode)).Append(',')
                   .Append(Csv(partner.CardName)).Append(',')
                   .Append(Csv(GetTypeLabel(partner.CardType))).Append(',')
                   .Append(Csv(partner.Email)).Append(',')
                   .Append(Csv(partner.Phone1)).Append(',')
                   .Append(Csv(partner.City)).Append(',')
                   .Append(Csv(partner.Country)).Append(',')
                   .Append(Csv(partner.Currency ?? "USD")).Append(',')
                   .Append((partner.Balance ?? 0m).ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                   .Append(partner.IsActive ? "Active" : "Inactive")
                   .Append('\n');
            }

            // The BOM is what makes Excel read the file as UTF-8; without it,
            // partner names with accents arrive mangled.
            var bytes = new UTF8Encoding(true).GetBytes(csv.ToString());
            var fileName = $"BusinessPartners_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

            await JS.InvokeVoidAsync("downloadFile", fileName, Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            errorMessage = ApiErrorResponse.GetFriendlyMessage(
                ex,
                "We couldn't export the directory right now. Please try again.");
        }
        finally
        {
            isExporting = false;
        }
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // A leading =, +, - or @ makes a spreadsheet treat the cell as a
        // formula; a leading apostrophe keeps it text.
        var text = "=+-@".Contains(value[0]) ? "'" + value : value;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }

    // ── Display ─────────────────────────────────────────────────────────────

    private static bool IsCustomer(string? type) => type is "C" or "cCustomer";

    private static bool IsSupplier(string? type) => type is "S" or "cSupplier";

    private static string GetTypeLabel(string? type) => type switch
    {
        "C" or "cCustomer" => "Customer",
        "S" or "cSupplier" => "Supplier",
        "L" or "cLead" => "Lead",
        _ => "Unknown"
    };

    private static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string Location(BusinessPartnerDto partner)
    {
        var parts = new[] { partner.City?.Trim(), partner.Country?.Trim() }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var text = string.Join(" ", parts);
        return text.Length == 0 ? "—" : text;
    }

    // The design leads the name column with the partner's initials.
    private static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "—";

        var words = new string(name.Where(c => char.IsLetter(c) || c == ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length == 0
            ? "—"
            : string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
    }

    // A minus rather than a bracket or a red: the design carries the sign in
    // the glyph and the weight of the colour, not in a second convention.
    private static string FormatBalance(decimal balance)
        => (balance < 0 ? "−" : string.Empty) + Math.Abs(balance).ToString("N2");

    private static string BalanceClass(decimal balance) => balance switch
    {
        < 0 => "bp-bal-credit",
        > 0 => "bp-bal-owed",
        _ => string.Empty
    };
}
