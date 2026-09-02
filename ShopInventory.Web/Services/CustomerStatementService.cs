using ShopInventory.Web.Models;
using ShopInventory.Web.Data;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// Interface for customer statement service
/// </summary>
public interface ICustomerStatementService
{
    Task<CustomerStatementResponse> GetStatementAsync(string cardCode, CustomerStatementRequest request);
    Task<byte[]> GenerateStatementPdfAsync(string cardCode, CustomerStatementRequest request);
    Task<CustomerDashboardSummary> GetDashboardSummaryAsync(string cardCode);
    Task<List<CustomerInvoiceSummary>> GetOpenInvoicesAsync(string cardCode, DateTime? fromDate = null, DateTime? toDate = null);
    Task<List<CustomerPaymentSummary>> GetPaymentHistoryAsync(string cardCode, DateTime? fromDate, DateTime? toDate);
    Task<AgingSummary> GetAgingSummaryAsync(string cardCode);
    Task<List<ItemCodeSummary>> GetItemCodeSummaryAsync(string cardCode, DateTime? fromDate = null, DateTime? toDate = null);
    Task<List<MonthlySpend>> GetMonthlySpendAsync(string cardCode, int months = 6);
}

/// <summary>
/// Customer statement service for portal functionality.
/// Supports both single-account and multi-account (main + sub) customer structures.
/// </summary>
public class CustomerStatementService : ICustomerStatementService
{
    private readonly HttpClient _httpClient;
    private readonly IBusinessPartnerService _businessPartnerService;
    private readonly IInvoiceService _invoiceService;
    private readonly ICustomerLinkedAccountService _linkedAccountService;
    private readonly ISalesOrderService _salesOrderService;
    private readonly ICreditNoteService _creditNoteService;
    private readonly ILogger<CustomerStatementService> _logger;

    public CustomerStatementService(
        HttpClient httpClient,
        IBusinessPartnerService businessPartnerService,
        IInvoiceService invoiceService,
        ICustomerLinkedAccountService linkedAccountService,
        ISalesOrderService salesOrderService,
        ICreditNoteService creditNoteService,
        ILogger<CustomerStatementService> logger)
    {
        _httpClient = httpClient;
        _businessPartnerService = businessPartnerService;
        _invoiceService = invoiceService;
        _linkedAccountService = linkedAccountService;
        _salesOrderService = salesOrderService;
        _creditNoteService = creditNoteService;
        _logger = logger;
    }

    /// <summary>
    /// Get customer statement with transaction details.
    /// For multi-account customers, aggregates invoices from all main accounts and payments from all main accounts.
    /// </summary>
    public async Task<CustomerStatementResponse> GetStatementAsync(string cardCode, CustomerStatementRequest request)
    {
        try
        {
            var url = BuildStatementUrl($"api/statement/{Uri.EscapeDataString(cardCode)}", request);
            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                CustomerStatementResponse? statement;
                try
                {
                    statement = await response.Content.ReadFromJsonAsync<CustomerStatementResponse>();
                }
                catch (JsonException ex)
                {
                    // A shape mismatch between this model and the API's DTO used to reach the
                    // customer verbatim — "The JSON value could not be converted to System.Int32.
                    // Path: $.customer.paymentTermsDays" — which told them nothing and hid the
                    // cause. Log the detail, show a sentence.
                    _logger.LogError(ex, "Statement response for {CardCode} did not match the expected shape", cardCode);
                    throw new InvalidOperationException(
                        "We couldn't read the statement the server sent back. Please contact support if this continues.");
                }

                if (statement == null)
                {
                    throw new InvalidOperationException("The server returned an empty statement response.");
                }

                return statement;
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to retrieve statement. Status: {StatusCode}, Error: {Error}", response.StatusCode, errorContent);
            throw new InvalidOperationException(ApiErrorResponse.GetFriendlyMessage(
                response.StatusCode,
                errorContent,
                "We couldn't load this statement right now. Please try again."));
        }
        catch (OperationCanceledException ex)
        {
            throw StillBuilding(ex, cardCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating statement for {CardCode}", cardCode);
            throw;
        }
    }

    /// <summary>
    /// Turns "the request was canceled due to the configured HttpClient.Timeout of 300 seconds
    /// elapsing" into something a customer can act on.
    /// </summary>
    /// <remarks>
    /// Since the API builds statements behind its own cache, on its own cancellation token, a
    /// timeout here no longer means the work was lost — the build carries on and the next attempt is
    /// answered from the cache, usually immediately. The raw message says the opposite: it reads as
    /// a dead end, so customers stopped rather than clicking Generate again, which is the one thing
    /// that would have worked.
    /// </remarks>
    private InvalidOperationException StillBuilding(Exception cause, string cardCode)
    {
        _logger.LogWarning(
            cause,
            "Statement request for {CardCode} exceeded the {TimeoutSeconds}s API budget; the build continues server-side",
            cardCode,
            _httpClient.Timeout.TotalSeconds);

        return new InvalidOperationException(
            "This statement is taking longer than usual to prepare. It is still being built — "
            + "please click Generate Statement again in a moment.");
    }

    /// <summary>
    /// Generate PDF statement
    /// </summary>
    public async Task<byte[]> GenerateStatementPdfAsync(string cardCode, CustomerStatementRequest request)
    {
        try
        {
            var url = BuildStatementUrl($"api/statement/generate/{Uri.EscapeDataString(cardCode)}", request);
            _logger.LogInformation("Requesting PDF from: {Url}", url);

            var response = await _httpClient.GetAsync(url);

            _logger.LogInformation("PDF response status: {StatusCode}, ContentType: {ContentType}",
                response.StatusCode, response.Content.Headers.ContentType?.MediaType);

            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                _logger.LogInformation("PDF bytes received: {Length} bytes", bytes.Length);
                return bytes;
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to generate PDF. Status: {StatusCode}, Error: {Error}",
                response.StatusCode, errorContent);
            throw new InvalidOperationException(ApiErrorResponse.GetFriendlyMessage(
                response.StatusCode,
                errorContent,
                "We couldn't generate this statement PDF right now. Please try again."));
        }
        catch (OperationCanceledException ex)
        {
            throw StillBuilding(ex, cardCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating PDF statement for {CardCode}", cardCode);
            throw;
        }
    }

    private static string BuildStatementUrl(string basePath, CustomerStatementRequest request)
    {
        var queryParams = new List<string>
        {
            $"fromDate={request.FromDate:yyyy-MM-dd}",
            $"toDate={request.ToDate:yyyy-MM-dd}"
        };

        foreach (var cardCode in request.CardCodes
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            queryParams.Add($"cardCodes={Uri.EscapeDataString(cardCode)}");
        }

        return $"{basePath}?{string.Join("&", queryParams)}";
    }

    /// <summary>
    /// Get customer dashboard summary.
    /// For multi-account customers, aggregates data across all main accounts (invoices/payments)
    /// and includes per-account breakdown with sub-account sales order counts.
    /// </summary>
    public async Task<CustomerDashboardSummary> GetDashboardSummaryAsync(string cardCode)
    {
        try
        {
            // Phase 1: Fetch customer info and account structure in parallel
            var customerTask = _businessPartnerService.GetBusinessPartnerByCodeAsync(cardCode);
            var accountStructureTask = _linkedAccountService.GetAccountStructureAsync(cardCode);
            await Task.WhenAll(customerTask, accountStructureTask);

            var customer = customerTask.Result;
            if (customer == null)
            {
                throw new InvalidOperationException("Customer not found");
            }

            var accountStructure = accountStructureTask.Result;
            var linkedAccounts = accountStructure == "Multi"
                ? await _linkedAccountService.GetLinkedAccountsAsync(cardCode)
                : new List<LinkedAccountInfo>();
            var allCardCodes = BuildDashboardCardCodes(cardCode, accountStructure, linkedAccounts);

            var summary = new CustomerDashboardSummary
            {
                Customer = new CustomerInfo
                {
                    CardCode = customer.CardCode ?? cardCode,
                    CardName = customer.CardName ?? "",
                    Email = customer.Email,
                    Phone = customer.Phone1,
                    Balance = customer.Balance ?? 0,
                    Currency = customer.Currency,
                    AccountStructure = accountStructure,
                    LinkedAccounts = linkedAccounts
                },
                AccountBalance = customer.Balance ?? 0
            };

            // Phase 2: Fetch invoices, payments, payment terms, and account breakdown in parallel
            var now = IAuditService.ToCAT(DateTime.UtcNow);
            var dashboardFrom = now.AddDays(-31);
            var invoicesByAccountTask = GetOpenInvoicesByAccountAsync(allCardCodes);
            var paymentsTask = GetPaymentHistoryForCardCodesAsync(allCardCodes, dashboardFrom, now);

            // Fetch payment terms for aging calculation
            var paymentTermsTask = customer.PayTermGrpCode.HasValue
                ? _businessPartnerService.GetPaymentTermsAsync(customer.PayTermGrpCode.Value)
                : Task.FromResult<PaymentTermsDto?>(null);

            await Task.WhenAll(invoicesByAccountTask, paymentsTask, paymentTermsTask);

            var invoicesByAccount = invoicesByAccountTask.Result;

            // The breakdown reads per-account balances and order counts from SAP, so it can only
            // start once the invoices it annotates are in hand.
            var accountBreakdown = (accountStructure == "Multi" && linkedAccounts.Any())
                ? await BuildAccountBreakdownAsync(linkedAccounts, invoicesByAccount)
                : new List<AccountSummary>();

            // Apply payment terms to aging calculation
            var paymentTerms = paymentTermsTask.Result;
            int paymentTermsDays = 0;
            if (paymentTerms != null)
            {
                paymentTermsDays = (paymentTerms.NumberOfAdditionalMonths * 30) + paymentTerms.NumberOfAdditionalDays;
                summary.Customer.PaymentTermsName = paymentTerms.PaymentTermsGroupName;
                summary.Customer.PaymentTermsDays = paymentTermsDays;
            }

            // Process invoices — recalculate DaysOverdue using payment terms (DocDate + terms)
            var openInvoices = FilterInvoices(
                invoicesByAccount.Values.SelectMany(invoices => invoices),
                dashboardFrom,
                now);

            if (paymentTermsDays > 0)
            {
                foreach (var invoice in openInvoices)
                {
                    invoice.DaysOverdue = CalculateDaysOverdueFromTerms(invoice.DocDate, paymentTermsDays);
                }
            }

            summary.OpenInvoicesCount = openInvoices.Count;
            summary.TotalOutstanding = openInvoices.Sum(i => i.Balance);
            summary.OverdueInvoicesCount = openInvoices.Count(i => i.DaysOverdue > 0);
            summary.OverdueAmount = openInvoices.Where(i => i.DaysOverdue > 0).Sum(i => i.Balance);
            summary.OldestOverdueDays = openInvoices.Count > 0 ? Math.Max(0, openInvoices.Max(i => i.DaysOverdue)) : 0;
            summary.RecentInvoices = openInvoices.Take(5).ToList();

            // Derive aging from already-fetched invoices (no extra SAP call)
            summary.Aging = CalculateAgingFromInvoices(openInvoices, paymentTermsDays);

            // Process payments
            var payments = paymentsTask.Result;
            if (payments.Any())
            {
                var lastPayment = payments.OrderByDescending(p => p.DocDate).First();
                summary.LastPaymentDate = lastPayment.DocDate;
                summary.LastPaymentAmount = lastPayment.DocTotal;
            }
            // Totalled over everything fetched, not over RecentPayments — that list is the
            // first five, and the dashboard states this as the period's receipts.
            summary.PaymentsReceived = payments.Sum(p => p.DocTotal);
            summary.RecentPayments = payments.Take(5).ToList();

            // Derive monthly spend from already-fetched invoices (no extra SAP call)
            var monthStart = new DateTime(now.Year, now.Month, 1);
            var currentMonthInvoiced = openInvoices
                .Where(i => i.DocDate >= monthStart && i.DocDate <= now)
                .Sum(i => i.DocTotal);
            summary.MonthlySpend = new List<MonthlySpend>
            {
                new() { Month = monthStart.ToString("MMM yyyy"), Invoiced = currentMonthInvoiced }
            };

            // Account breakdown for multi-account customers
            if (accountStructure == "Multi" && linkedAccounts.Any())
            {
                summary.AccountBreakdown = accountBreakdown;

                var mainAccountBalances = summary.AccountBreakdown
                    .Where(a => a.AccountType == "Main")
                    .Sum(a => a.Balance);
                if (mainAccountBalances != 0)
                {
                    summary.AccountBalance = mainAccountBalances;
                }
            }

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dashboard summary for {CardCode}", cardCode);
            throw;
        }
    }

    /// <summary>
    /// Calculate aging buckets from an already-fetched list of open invoices.
    /// Bucket boundaries are based on customer payment terms (e.g., 7-day terms → 1-7, 8-14, 15-21, 21+).
    /// Falls back to standard 30-day buckets when payment terms are 0 or not set.
    /// </summary>
    private static AgingSummary CalculateAgingFromInvoices(List<CustomerInvoiceSummary> openInvoices, int paymentTermsDays = 0)
    {
        var aging = new AgingSummary();
        int bucket = paymentTermsDays > 0 ? paymentTermsDays : 30;

        aging.Bucket1Label = $"1-{bucket} Days";
        aging.Bucket2Label = $"{bucket + 1}-{bucket * 2} Days";
        aging.Bucket3Label = $"{bucket * 2 + 1}-{bucket * 3} Days";
        aging.Bucket4Label = $"Over {bucket * 3} Days";

        foreach (var invoice in openInvoices)
        {
            var daysOverdue = invoice.DaysOverdue;

            if (daysOverdue <= 0)
            {
                aging.Current += invoice.Balance;
                aging.CurrentCount++;
            }
            else if (daysOverdue <= bucket)
            {
                aging.Days1To30 += invoice.Balance;
                aging.Bucket1Count++;
            }
            else if (daysOverdue <= bucket * 2)
            {
                aging.Days31To60 += invoice.Balance;
                aging.Bucket2Count++;
            }
            else if (daysOverdue <= bucket * 3)
            {
                aging.Days61To90 += invoice.Balance;
                aging.Bucket3Count++;
            }
            else
            {
                aging.Over90Days += invoice.Balance;
                aging.Bucket4Count++;
            }
        }

        aging.Total = aging.Current + aging.Days1To30 + aging.Days31To60 + aging.Days61To90 + aging.Over90Days;
        return aging;
    }

    /// <summary>
    /// Build per-account breakdown showing individual balances and transaction counts.
    /// Fetches all linked accounts in parallel.
    /// </summary>
    /// <param name="linkedAccounts">
    /// The accounts to break down. Each one costs a business partner read and a sales order read,
    /// run in parallel across the set, so this grows with the number of accounts a customer has
    /// linked rather than with the size of their ledger.
    /// </param>
    /// <param name="invoicesByAccount">
    /// Open invoices already read for the whole account set, keyed by card code. Passed in rather
    /// than fetched here so the breakdown costs no extra SAP reads.
    /// </param>
    private async Task<List<AccountSummary>> BuildAccountBreakdownAsync(
        List<LinkedAccountInfo> linkedAccounts,
        IReadOnlyDictionary<string, List<CustomerInvoiceSummary>> invoicesByAccount)
    {
        var tasks = linkedAccounts.Select(async account =>
        {
            var acctSummary = new AccountSummary
            {
                CardCode = account.CardCode,
                CardName = account.CardName,
                AccountType = account.AccountType,
                Currency = account.Currency,
                Description = account.Description,
                AllowedTransactions = account.AllowedTransactions
            };

            try
            {
                // Fetch BP and sales orders in parallel within each account. The invoices are
                // already in hand.
                var partnerTask = _businessPartnerService.GetBusinessPartnerByCodeAsync(account.CardCode);
                var ordersTask = account.AccountType == "Sub"
                    ? _salesOrderService.GetSalesOrdersAsync(cardCode: account.CardCode, status: SalesOrderStatus.Pending)
                    : Task.FromResult<SalesOrderListResponse?>(null);

                await Task.WhenAll(partnerTask, ordersTask);

                acctSummary.Balance = partnerTask.Result?.Balance ?? 0;

                var invoices = invoicesByAccount.TryGetValue(account.CardCode, out var accountInvoices)
                    ? accountInvoices
                    : new List<CustomerInvoiceSummary>();
                acctSummary.OpenInvoicesCount = invoices.Count;
                acctSummary.TotalOutstanding = invoices.Sum(i => i.Balance);

                if (account.AccountType == "Sub")
                {
                    acctSummary.OpenSalesOrdersCount = ordersTask.Result?.TotalCount ?? 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching data for linked account {CardCode}", account.CardCode);
            }

            return acctSummary;
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private static List<string> BuildDashboardCardCodes(
        string cardCode,
        string accountStructure,
        IReadOnlyCollection<LinkedAccountInfo> linkedAccounts)
    {
        if (accountStructure != "Multi" || linkedAccounts.Count == 0)
        {
            return new List<string> { cardCode };
        }

        return linkedAccounts
            .Where(account => !string.IsNullOrWhiteSpace(account.CardCode))
            .Select(account => account.CardCode)
            .Append(cardCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<CustomerInvoiceSummary> FilterInvoices(
        IEnumerable<CustomerInvoiceSummary> invoices,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var result = invoices;

        if (fromDate.HasValue)
            result = result.Where(i => i.DocDate >= fromDate.Value);

        if (toDate.HasValue)
            result = result.Where(i => i.DocDate <= toDate.Value);

        return result
            .OrderBy(i => i.DueDate)
            .ToList();
    }

    /// <summary>
    /// Build item code summary by aggregating invoice line items and subtracting credit note line items.
    /// For multi-account customers, aggregates across all main accounts.
    /// </summary>
    public async Task<List<ItemCodeSummary>> GetItemCodeSummaryAsync(string cardCode, DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            var allCardCodes = await _linkedAccountService.GetAllCardCodesAsync(cardCode);
            var itemMap = new Dictionary<string, ItemCodeSummary>(StringComparer.OrdinalIgnoreCase);
            var accountDataTasks = allCardCodes.Select(async accountCardCode =>
            {
                // The lines are the whole point here � the summary aggregates them by item code �
                // and both lists answer with headers only unless asked.
                var invoiceTask = _invoiceService.GetInvoicesByCustomerAsync(
                    accountCardCode,
                    fromDate,
                    toDate,
                    includeLines: true);
                var creditNoteTask = _creditNoteService.GetCreditNotesAsync(
                    page: 1,
                    pageSize: 1000,
                    cardCode: accountCardCode,
                    fromDate: fromDate,
                    toDate: toDate,
                    includeLines: true);
                await Task.WhenAll(invoiceTask, creditNoteTask);
                return (
                    Invoices: invoiceTask.Result?.Invoices ?? [],
                    CreditNotes: creditNoteTask.Result?.CreditNotes ?? []);
            });
            var accountData = await Task.WhenAll(accountDataTasks);

            // Aggregate invoice line items across all accounts (main + sub)
            foreach (var data in accountData)
            {
                foreach (var invoice in data.Invoices)
                {
                    if (invoice.Lines == null) continue;

                    foreach (var line in invoice.Lines)
                    {
                        if (string.IsNullOrEmpty(line.ItemCode)) continue;

                        if (!itemMap.TryGetValue(line.ItemCode, out var summary))
                        {
                            summary = new ItemCodeSummary
                            {
                                ItemCode = line.ItemCode,
                                ItemDescription = line.ItemDescription,
                                ItemGroup = DeriveItemGroup(line.ItemDescription)
                            };
                            itemMap[line.ItemCode] = summary;
                        }

                        summary.InvoicedQuantity += line.Quantity;
                        summary.InvoicedAmount += line.LineTotal;
                        summary.InvoiceCount++;

                        // Keep the most descriptive item description
                        if (string.IsNullOrEmpty(summary.ItemDescription) && !string.IsNullOrEmpty(line.ItemDescription))
                        {
                            summary.ItemDescription = line.ItemDescription;
                            summary.ItemGroup = DeriveItemGroup(line.ItemDescription);
                        }
                    }
                }

                // Aggregate credit note line items for the same account
                foreach (var cn in data.CreditNotes)
                {
                    // Only count non-cancelled/voided credit notes
                    if (cn.Status == CreditNoteStatus.Cancelled || cn.Status == CreditNoteStatus.Voided)
                        continue;

                    foreach (var line in cn.Lines)
                    {
                        if (string.IsNullOrEmpty(line.ItemCode)) continue;

                        if (!itemMap.TryGetValue(line.ItemCode, out var summary))
                        {
                            summary = new ItemCodeSummary
                            {
                                ItemCode = line.ItemCode,
                                ItemDescription = line.ItemDescription,
                                ItemGroup = DeriveItemGroup(line.ItemDescription)
                            };
                            itemMap[line.ItemCode] = summary;
                        }

                        summary.CreditedQuantity += line.Quantity;
                        summary.CreditedAmount += line.LineTotal;
                        summary.CreditNoteCount++;

                        if (string.IsNullOrEmpty(summary.ItemDescription) && !string.IsNullOrEmpty(line.ItemDescription))
                        {
                            summary.ItemDescription = line.ItemDescription;
                            summary.ItemGroup = DeriveItemGroup(line.ItemDescription);
                        }
                    }
                }
            }

            // Invoices with no lines summarise to nothing, and an empty summary is
            // indistinguishable from a customer who bought nothing — which is exactly
            // how this page spent its time reporting "No invoiced items in the selected
            // period" while the invoices were there. Say so in the log instead.
            if (itemMap.Count == 0)
            {
                var invoiceCount = accountData.Sum(data => data.Invoices.Count);
                if (invoiceCount > 0)
                {
                    _logger.LogWarning(
                        "Item summary for {CardCode} found {InvoiceCount} invoice(s) between {From:yyyy-MM-dd} and " +
                        "{To:yyyy-MM-dd} but no document lines on any of them — the list was asked for without lines",
                        cardCode,
                        invoiceCount,
                        fromDate,
                        toDate);
                }
            }

            return itemMap.Values
                .OrderByDescending(s => s.NetAmount)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building item code summary for {CardCode}", cardCode);
            return new List<ItemCodeSummary>();
        }
    }

    /// <summary>
    /// Derives a product category/group from the item description.
    /// Uses the first word of the description as a rough grouping.
    /// </summary>
    private static string DeriveItemGroup(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "Other";

        // Take the first word as the group name
        var trimmed = description.Trim();
        var spaceIndex = trimmed.IndexOf(' ');
        var group = spaceIndex > 0 ? trimmed[..spaceIndex] : trimmed;

        // Capitalise first letter
        return char.ToUpper(group[0]) + group[1..].ToLower();
    }

    /// <summary>
    /// Get open (unpaid) invoices for customer.
    /// For multi-account customers, aggregates invoices from all accounts (main + sub).
    /// </summary>
    public async Task<List<CustomerInvoiceSummary>> GetOpenInvoicesAsync(string cardCode, DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            var allCardCodes = await _linkedAccountService.GetAllCardCodesAsync(cardCode);
            return await GetOpenInvoicesForCardCodesAsync(allCardCodes, fromDate, toDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting open invoices for {CardCode}", cardCode);
            return new List<CustomerInvoiceSummary>();
        }
    }

    private async Task<List<CustomerInvoiceSummary>> GetOpenInvoicesForCardCodesAsync(
        IEnumerable<string> cardCodes,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var byAccount = await GetOpenInvoicesByAccountAsync(cardCodes);

        return FilterInvoices(byAccount.Values.SelectMany(invoices => invoices), fromDate, toDate);
    }

    /// <summary>
    /// Open invoices for a set of accounts, keyed by the account they belong to. Every requested
    /// card code is present in the result, with an empty list when it owes nothing.
    /// </summary>
    /// <remarks>
    /// One bounded SAP read for the whole set. This used to be a fan-out — a task per account, each
    /// calling the by-customer endpoint with no dates, which on the API side falls through to
    /// "every invoice this account has ever had" and pages until exhausted. The portal then kept
    /// only the few still carrying a balance. For an account trading daily since the system went in,
    /// that is its entire history pulled across to answer "what is outstanding", once per linked
    /// account, on the dashboard, the invoices page and the aging summary.
    ///
    /// SAP filters on document status now, and the card codes go into one request, so the cost is
    /// proportional to what is actually owed rather than to how long the customer has been trading.
    /// </remarks>
    private async Task<Dictionary<string, List<CustomerInvoiceSummary>>> GetOpenInvoicesByAccountAsync(
        IEnumerable<string> cardCodes)
    {
        var codes = cardCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byAccount = codes.ToDictionary(
            code => code,
            _ => new List<CustomerInvoiceSummary>(),
            StringComparer.OrdinalIgnoreCase);

        if (codes.Count == 0)
        {
            return byAccount;
        }

        try
        {
            var response = await _invoiceService.GetOpenInvoicesByCustomersAsync(codes);

            foreach (var invoice in response?.Invoices ?? new List<InvoiceDto>())
            {
                var cardCode = invoice.CardCode;
                if (string.IsNullOrWhiteSpace(cardCode) || !byAccount.TryGetValue(cardCode, out var accountInvoices))
                {
                    continue;
                }

                var summary = new CustomerInvoiceSummary
                {
                    DocEntry = invoice.DocEntry,
                    DocNum = invoice.DocNum,
                    CardCode = cardCode,
                    CardName = invoice.CardName,
                    DocDate = ParseDate(invoice.DocDate),
                    DueDate = ParseNullableDate(invoice.DocDueDate),
                    DocTotal = invoice.DocTotal,
                    PaidToDate = invoice.PaidToDate,
                    Balance = invoice.DocTotal - invoice.PaidToDate,
                    Currency = invoice.DocCurrency,
                    Status = GetInvoiceStatus(invoice.DocStatus),
                    DaysOverdue = CalculateDaysOverdue(invoice.DocDueDate)
                };

                if (summary.Balance > 0)
                {
                    accountInvoices.Add(summary);
                }
            }

            foreach (var accountInvoices in byAccount.Values)
            {
                accountInvoices.Sort((left, right) => Nullable.Compare(left.DueDate, right.DueDate));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting open invoices for {CardCodes}", string.Join(",", codes));
        }

        return byAccount;
    }

    /// <summary>
    /// Get payment history for customer.
    /// For multi-account customers, aggregates payments from all accounts (main + sub).
    /// </summary>
    public async Task<List<CustomerPaymentSummary>> GetPaymentHistoryAsync(string cardCode, DateTime? fromDate, DateTime? toDate)
    {
        try
        {
            var allCardCodes = await _linkedAccountService.GetAllCardCodesAsync(cardCode);
            return await GetPaymentHistoryForCardCodesAsync(allCardCodes, fromDate, toDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payment history for {CardCode}", cardCode);
            return new List<CustomerPaymentSummary>();
        }
    }

    private async Task<List<CustomerPaymentSummary>> GetPaymentHistoryForCardCodesAsync(
        IEnumerable<string> cardCodes,
        DateTime? fromDate,
        DateTime? toDate)
    {
        var distinctCardCodes = cardCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var paymentTasks = distinctCardCodes.Select(async acctCardCode =>
        {
            var url = $"api/incomingpayment/customer/{Uri.EscapeDataString(acctCardCode)}";
            var queryParams = new List<string>();

            if (fromDate.HasValue)
                queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue)
                queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");

            if (queryParams.Count > 0)
                url += "?" + string.Join("&", queryParams);

            var response = await _httpClient.GetFromJsonAsync<IncomingPaymentDateResponse>(url);
            var payments = response?.Payments ?? new List<IncomingPaymentDto>();

            return payments
                .Select(p => new CustomerPaymentSummary
                {
                    DocEntry = p.DocEntry,
                    DocNum = p.DocNum,
                    DocDate = ParseDate(p.DocDate),
                    DocTotal = p.DocTotal,
                    PaymentMethod = DeterminePaymentMethod(p),
                    Reference = p.TransferReference ?? p.Remarks,
                    Currency = p.DocCurrency,
                    CardCode = acctCardCode
                }).ToList();
        });

        var paymentResults = await Task.WhenAll(paymentTasks);
        var allPayments = paymentResults.SelectMany(r => r).ToList();

        IEnumerable<CustomerPaymentSummary> result = allPayments;

        if (fromDate.HasValue)
            result = result.Where(p => p.DocDate >= fromDate.Value);

        if (toDate.HasValue)
            result = result.Where(p => p.DocDate <= toDate.Value);

        return result.OrderByDescending(p => p.DocDate).ToList();
    }

    /// <summary>
    /// Get aging summary with dynamic buckets based on customer payment terms.
    /// </summary>
    public async Task<AgingSummary> GetAgingSummaryAsync(string cardCode)
    {
        try
        {
            var openInvoices = await GetOpenInvoicesAsync(cardCode);

            // Fetch payment terms for dynamic aging buckets
            int paymentTermsDays = 0;
            var customer = await _businessPartnerService.GetBusinessPartnerByCodeAsync(cardCode);
            if (customer?.PayTermGrpCode.HasValue == true)
            {
                var paymentTerms = await _businessPartnerService.GetPaymentTermsAsync(customer.PayTermGrpCode.Value);
                if (paymentTerms != null)
                {
                    paymentTermsDays = (paymentTerms.NumberOfAdditionalMonths * 30) + paymentTerms.NumberOfAdditionalDays;

                    // Recalculate DaysOverdue using payment terms
                    if (paymentTermsDays > 0)
                    {
                        foreach (var invoice in openInvoices)
                        {
                            invoice.DaysOverdue = CalculateDaysOverdueFromTerms(invoice.DocDate, paymentTermsDays);
                        }
                    }
                }
            }

            return CalculateAgingFromInvoices(openInvoices, paymentTermsDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating aging for {CardCode}", cardCode);
            return new AgingSummary();
        }
    }

    #region Private Helper Methods

    private async Task<List<CustomerInvoiceSummary>> GetInvoicesForPeriodAsync(
        string cardCode, DateTime fromDate, DateTime toDate, bool includeClosedInvoices)
    {
        try
        {
            var invoiceResponse = await _invoiceService.GetInvoicesByCustomerAsync(cardCode);
            var invoices = invoiceResponse?.Invoices ?? new List<InvoiceDto>();

            return invoices
                .Select(i =>
                {
                    var docDate = ParseDate(i.DocDate);
                    return new { Invoice = i, DocDate = docDate };
                })
                .Where(x => x.DocDate != DateTime.MinValue)
                .Where(x => x.DocDate.Date >= fromDate.Date && x.DocDate.Date <= toDate.Date)
                .Where(x => x.Invoice.DocStatus != "X")
                .Where(x => includeClosedInvoices || x.Invoice.DocStatus != "C")
                .Select(x => new CustomerInvoiceSummary
                {
                    DocEntry = x.Invoice.DocEntry,
                    DocNum = x.Invoice.DocNum,
                    CardCode = cardCode,
                    CardName = x.Invoice.CardName,
                    DocDate = x.DocDate,
                    DueDate = ParseNullableDate(x.Invoice.DocDueDate),
                    DocTotal = x.Invoice.DocTotal,
                    PaidToDate = x.Invoice.PaidToDate,
                    Balance = x.Invoice.DocTotal - x.Invoice.PaidToDate,
                    Currency = x.Invoice.DocCurrency,
                    Status = GetInvoiceStatus(x.Invoice.DocStatus),
                    DaysOverdue = CalculateDaysOverdue(x.Invoice.DocDueDate)
                })
                .OrderBy(i => i.DocDate)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting invoices for period");
            return new List<CustomerInvoiceSummary>();
        }
    }

    private async Task<List<CreditNoteDto>> GetCreditNotesForPeriodAsync(
        string cardCode, DateTime fromDate, DateTime toDate)
    {
        try
        {
            var response = await _creditNoteService.GetCreditNotesAsync(
                page: 1,
                pageSize: 1000,
                cardCode: cardCode,
                fromDate: fromDate,
                toDate: toDate);

            var creditNotes = response?.CreditNotes ?? new List<CreditNoteDto>();
            return creditNotes
                .Where(c => c.Status != CreditNoteStatus.Cancelled && c.Status != CreditNoteStatus.Voided)
                .Where(c => c.CreditNoteDate >= fromDate && c.CreditNoteDate <= toDate)
                .OrderBy(c => c.CreditNoteDate)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting credit notes for statement period");
            return new List<CreditNoteDto>();
        }
    }

    private async Task<List<CustomerPaymentSummary>> GetPaymentsForPeriodAsync(
        string cardCode, DateTime fromDate, DateTime toDate)
    {
        return await GetPaymentHistoryForCardCodesAsync(new[] { cardCode }, fromDate, toDate);
    }

    private static DateTime ParseDate(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr))
            return DateTime.MinValue;

        if (DateTime.TryParse(dateStr, out var date))
            return date;

        return DateTime.MinValue;
    }

    private static DateTime? ParseNullableDate(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr))
            return null;

        if (DateTime.TryParse(dateStr, out var date))
            return date;

        return null;
    }

    private static string GetInvoiceStatus(string? status)
    {
        return status switch
        {
            "O" => "Open",
            "C" => "Closed",
            "X" => "Cancelled",
            _ => "Unknown"
        };
    }

    private static int CalculateDaysOverdue(string? dueDateStr)
    {
        var dueDate = ParseNullableDate(dueDateStr);
        if (!dueDate.HasValue)
            return 0;

        var days = (DateTime.Today - dueDate.Value).Days;
        return days > 0 ? days : 0;
    }

    /// <summary>
    /// Calculate days overdue using payment terms: effective due date = DocDate + payment terms days.
    /// </summary>
    private static int CalculateDaysOverdueFromTerms(DateTime docDate, int paymentTermsDays)
    {
        if (docDate == DateTime.MinValue)
            return 0;

        var effectiveDueDate = docDate.AddDays(paymentTermsDays);
        var days = (DateTime.Today - effectiveDueDate).Days;
        return days > 0 ? days : 0;
    }

    private static string DeterminePaymentMethod(IncomingPaymentDto payment)
    {
        if (payment.CashSum > 0) return "Cash";
        if (payment.CheckSum > 0) return "Check";
        if (payment.TransferSum > 0) return "Transfer";
        if (payment.CreditSum > 0) return "Credit Card";
        return "Other";
    }
    #endregion

    public async Task<List<MonthlySpend>> GetMonthlySpendAsync(string cardCode, int months = 6)
    {
        try
        {
            var allCardCodes = await _linkedAccountService.GetAllCardCodesAsync(cardCode);
            var now = IAuditService.ToCAT(DateTime.UtcNow);
            var fromDate = new DateTime(now.Year, now.Month, 1).AddMonths(-(months - 1));
            var toDate = now;

            // Fetch all accounts in parallel
            var spendTasks = allCardCodes.Select(async acctCardCode =>
            {
                var response = await _invoiceService.GetInvoicesByCustomerAsync(acctCardCode, fromDate, toDate);
                if (response?.Invoices != null)
                {
                    return response.Invoices.Select(inv => new CustomerInvoiceSummary
                    {
                        DocDate = DateTime.TryParse(inv.DocDate?.ToString(), out var d) ? d : DateTime.MinValue,
                        DocTotal = inv.DocTotal
                    }).ToList();
                }
                return new List<CustomerInvoiceSummary>();
            });
            var spendResults = await Task.WhenAll(spendTasks);
            var allInvoices = spendResults.SelectMany(r => r).ToList();

            var result = new List<MonthlySpend>();
            for (int i = 0; i < months; i++)
            {
                var monthStart = fromDate.AddMonths(i);
                var monthEnd = monthStart.AddMonths(1).AddDays(-1);
                var label = monthStart.ToString("MMM yyyy");

                var monthInvoiced = allInvoices
                    .Where(inv => inv.DocDate >= monthStart && inv.DocDate <= monthEnd)
                    .Sum(inv => inv.DocTotal);

                result.Add(new MonthlySpend
                {
                    Month = label,
                    Invoiced = monthInvoiced
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating monthly spend for {CardCode}", cardCode);
            return new List<MonthlySpend>();
        }
    }
}
