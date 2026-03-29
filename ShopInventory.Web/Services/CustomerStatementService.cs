using ShopInventory.Web.Models;
using ShopInventory.Web.Data;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;

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
            // Get customer info
            var customer = await _businessPartnerService.GetBusinessPartnerByCodeAsync(cardCode);
            if (customer == null)
            {
                throw new InvalidOperationException("Customer not found");
            }

            var accountStructure = await _linkedAccountService.GetAccountStructureAsync(cardCode);

            var response = new CustomerStatementResponse
            {
                Customer = new CustomerInfo
                {
                    CardCode = customer.CardCode ?? cardCode,
                    CardName = customer.CardName ?? "",
                    Email = customer.Email,
                    Phone = customer.Phone1,
                    Balance = customer.Balance ?? 0,
                    Currency = customer.Currency,
                    AccountStructure = accountStructure
                },
                FromDate = request.FromDate,
                ToDate = request.ToDate,
                GeneratedAt = IAuditService.ToCAT(DateTime.UtcNow),
                Lines = new List<StatementLine>()
            };

            // For multi-account: get invoices/payments from all main accounts
            // For single-account: just use the one cardCode
            var invoiceCardCodes = await _linkedAccountService.GetMainAccountCardCodesAsync(cardCode);
            var allInvoices = new List<CustomerInvoiceSummary>();
            var allPayments = new List<CustomerPaymentSummary>();

            foreach (var invoiceCardCode in invoiceCardCodes)
            {
                var invoices = await GetInvoicesForPeriodAsync(invoiceCardCode, request.FromDate, request.ToDate, request.IncludeClosedInvoices);
                allInvoices.AddRange(invoices);

                var payments = await GetPaymentsForPeriodAsync(invoiceCardCode, request.FromDate, request.ToDate);
                allPayments.AddRange(payments);
            }

            // Combine and sort transactions
            var allTransactions = new List<(DateTime Date, string Type, StatementLine Line)>();

            foreach (var invoice in allInvoices)
            {
                allTransactions.Add((invoice.DocDate, "Invoice", new StatementLine
                {
                    Date = invoice.DocDate,
                    DocumentType = "Invoice",
                    DocumentNumber = invoice.DocNum.ToString(),
                    Description = $"Invoice #{invoice.DocNum}",
                    Debit = invoice.DocTotal,
                    Credit = 0,
                    Currency = invoice.Currency,
                    Status = invoice.Status,
                    DaysOverdue = invoice.DaysOverdue
                }));
            }

            foreach (var payment in allPayments)
            {
                allTransactions.Add((payment.DocDate, "Payment", new StatementLine
                {
                    Date = payment.DocDate,
                    DocumentType = "Payment",
                    DocumentNumber = payment.DocNum.ToString(),
                    Reference = payment.Reference,
                    Description = $"Payment - {payment.PaymentMethod}",
                    Debit = 0,
                    Credit = payment.DocTotal,
                    Currency = payment.Currency
                }));
            }

            // Sort by date
            allTransactions = allTransactions.OrderBy(t => t.Date).ToList();

            // Calculate running balance
            decimal runningBalance = response.OpeningBalance;
            foreach (var (_, _, line) in allTransactions)
            {
                runningBalance += line.Debit - line.Credit;
                line.Balance = runningBalance;
                response.Lines.Add(line);
            }

            // Calculate totals
            response.TotalInvoices = response.Lines.Where(l => l.DocumentType == "Invoice").Sum(l => l.Debit);
            response.TotalPayments = response.Lines.Where(l => l.DocumentType == "Payment").Sum(l => l.Credit);
            response.TotalCreditNotes = response.Lines.Where(l => l.DocumentType == "Credit Note").Sum(l => l.Credit);
            response.ClosingBalance = runningBalance;

            // Get aging summary (aggregated across all main accounts)
            response.Aging = await GetAgingSummaryAsync(cardCode);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating statement for {CardCode}", cardCode);
            throw;
        }
    }

    /// <summary>
    /// Generate PDF statement
    /// </summary>
    public async Task<byte[]> GenerateStatementPdfAsync(string cardCode, CustomerStatementRequest request)
    {
        try
        {
            var url = $"api/statement/generate/{cardCode}?fromDate={request.FromDate:yyyy-MM-dd}&toDate={request.ToDate:yyyy-MM-dd}";
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
            throw new InvalidOperationException($"Failed to generate statement PDF: {response.StatusCode} - {errorContent}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating PDF statement for {CardCode}", cardCode);
            throw;
        }
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

            // Phase 2: Fetch invoices, payments, monthly spend, payment terms, and account breakdown in parallel
            var invoicesTask = GetOpenInvoicesAsync(cardCode);
            var paymentsTask = GetPaymentHistoryAsync(cardCode, IAuditService.ToCAT(DateTime.UtcNow).AddMonths(-3), IAuditService.ToCAT(DateTime.UtcNow));
            var monthlySpendTask = GetMonthlySpendAsync(cardCode);

            // Fetch payment terms for aging calculation
            var paymentTermsTask = customer.PayTermGrpCode.HasValue
                ? _businessPartnerService.GetPaymentTermsAsync(customer.PayTermGrpCode.Value)
                : Task.FromResult<PaymentTermsDto?>(null);

            var breakdownTask = (accountStructure == "Multi" && linkedAccounts.Any())
                ? BuildAccountBreakdownAsync(linkedAccounts)
                : Task.FromResult(new List<AccountSummary>());

            await Task.WhenAll(invoicesTask, paymentsTask, monthlySpendTask, paymentTermsTask, breakdownTask);

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
            var openInvoices = invoicesTask.Result;

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
            summary.RecentInvoices = openInvoices.Take(5).ToList();

            // Derive aging from already-fetched invoices (no extra SAP call)
            summary.Aging = CalculateAgingFromInvoices(openInvoices);

            // Process payments
            var payments = paymentsTask.Result;
            if (payments.Any())
            {
                var lastPayment = payments.OrderByDescending(p => p.DocDate).First();
                summary.LastPaymentDate = lastPayment.DocDate;
                summary.LastPaymentAmount = lastPayment.DocTotal;
            }
            summary.RecentPayments = payments.Take(5).ToList();

            // Monthly spend chart data
            summary.MonthlySpend = monthlySpendTask.Result;

            // Account breakdown for multi-account customers
            if (accountStructure == "Multi" && linkedAccounts.Any())
            {
                summary.AccountBreakdown = breakdownTask.Result;

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
    /// </summary>
    private static AgingSummary CalculateAgingFromInvoices(List<CustomerInvoiceSummary> openInvoices)
    {
        var aging = new AgingSummary();

        foreach (var invoice in openInvoices)
        {
            var daysOverdue = invoice.DaysOverdue;

            if (daysOverdue <= 0)
                aging.Current += invoice.Balance;
            else if (daysOverdue <= 30)
                aging.Days1To30 += invoice.Balance;
            else if (daysOverdue <= 60)
                aging.Days31To60 += invoice.Balance;
            else if (daysOverdue <= 90)
                aging.Days61To90 += invoice.Balance;
            else
                aging.Over90Days += invoice.Balance;
        }

        aging.Total = aging.Current + aging.Days1To30 + aging.Days31To60 + aging.Days61To90 + aging.Over90Days;
        return aging;
    }

    /// <summary>
    /// Build per-account breakdown showing individual balances and transaction counts.
    /// Fetches all linked accounts in parallel.
    /// </summary>
    private async Task<List<AccountSummary>> BuildAccountBreakdownAsync(List<LinkedAccountInfo> linkedAccounts)
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
                var partner = await _businessPartnerService.GetBusinessPartnerByCodeAsync(account.CardCode);
                acctSummary.Balance = partner?.Balance ?? 0;

                if (account.AccountType == "Main")
                {
                    var invoices = await GetOpenInvoicesForSingleAccountAsync(account.CardCode);
                    acctSummary.OpenInvoicesCount = invoices.Count;
                    acctSummary.TotalOutstanding = invoices.Sum(i => i.Balance);
                }
                else if (account.AccountType == "Sub")
                {
                    try
                    {
                        var orders = await _salesOrderService.GetSalesOrdersAsync(
                            cardCode: account.CardCode, status: SalesOrderStatus.Pending);
                        acctSummary.OpenSalesOrdersCount = orders?.TotalCount ?? 0;
                    }
                    catch
                    {
                        acctSummary.OpenSalesOrdersCount = 0;
                    }
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

    /// <summary>
    /// Build item code summary by aggregating invoice line items and subtracting credit note line items.
    /// For multi-account customers, aggregates across all main accounts.
    /// </summary>
    public async Task<List<ItemCodeSummary>> GetItemCodeSummaryAsync(string cardCode, DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            var mainCardCodes = await _linkedAccountService.GetMainAccountCardCodesAsync(cardCode);
            var itemMap = new Dictionary<string, ItemCodeSummary>(StringComparer.OrdinalIgnoreCase);

            // Aggregate invoice line items across all main accounts
            foreach (var mainCardCode in mainCardCodes)
            {
                var invoiceResponse = await _invoiceService.GetInvoicesByCustomerAsync(mainCardCode, fromDate, toDate);
                var invoices = invoiceResponse?.Invoices ?? new List<InvoiceDto>();

                foreach (var invoice in invoices)
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
                var creditNotesResponse = await _creditNoteService.GetCreditNotesAsync(
                    page: 1, pageSize: 1000, cardCode: mainCardCode, fromDate: fromDate, toDate: toDate);
                var creditNotes = creditNotesResponse?.CreditNotes ?? new List<CreditNoteDto>();

                foreach (var cn in creditNotes)
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
    /// For multi-account customers, aggregates invoices from all main accounts.
    /// </summary>
    public async Task<List<CustomerInvoiceSummary>> GetOpenInvoicesAsync(string cardCode, DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            // Get all main account CardCodes (for single account, this returns just the one cardCode)
            var mainCardCodes = await _linkedAccountService.GetMainAccountCardCodesAsync(cardCode);
            var allInvoices = new List<CustomerInvoiceSummary>();

            foreach (var mainCardCode in mainCardCodes)
            {
                var invoices = await GetOpenInvoicesForSingleAccountAsync(mainCardCode);
                allInvoices.AddRange(invoices);
            }

            IEnumerable<CustomerInvoiceSummary> result = allInvoices;

            if (fromDate.HasValue)
                result = result.Where(i => i.DocDate >= fromDate.Value);

            if (toDate.HasValue)
                result = result.Where(i => i.DocDate <= toDate.Value);

            return result
                .OrderBy(i => i.DueDate)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting open invoices for {CardCode}", cardCode);
            return new List<CustomerInvoiceSummary>();
        }
    }

    /// <summary>
    /// Get open invoices for a single specific account CardCode (not aggregated)
    /// </summary>
    private async Task<List<CustomerInvoiceSummary>> GetOpenInvoicesForSingleAccountAsync(string singleCardCode)
    {
        try
        {
            var invoiceResponse = await _invoiceService.GetInvoicesByCustomerAsync(singleCardCode);
            var invoices = invoiceResponse?.Invoices ?? new List<InvoiceDto>();

            return invoices
                .Where(i => i.DocStatus != "C" && i.DocStatus != "X")
                .Select(i => new CustomerInvoiceSummary
                {
                    DocEntry = i.DocEntry,
                    DocNum = i.DocNum,
                    CardCode = singleCardCode,
                    CardName = i.CardName,
                    DocDate = ParseDate(i.DocDate),
                    DueDate = ParseNullableDate(i.DocDueDate),
                    DocTotal = i.DocTotal,
                    PaidToDate = i.PaidToDate,
                    Balance = i.DocTotal - i.PaidToDate,
                    Currency = i.DocCurrency,
                    Status = GetInvoiceStatus(i.DocStatus),
                    DaysOverdue = CalculateDaysOverdue(i.DocDueDate)
                })
                .Where(i => i.Balance > 0)
                .OrderBy(i => i.DueDate)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting open invoices for single account {CardCode}", singleCardCode);
            return new List<CustomerInvoiceSummary>();
        }
    }

    /// <summary>
    /// Get payment history for customer.
    /// For multi-account customers, aggregates payments from all main accounts.
    /// </summary>
    public async Task<List<CustomerPaymentSummary>> GetPaymentHistoryAsync(string cardCode, DateTime? fromDate, DateTime? toDate)
    {
        try
        {
            var mainCardCodes = await _linkedAccountService.GetMainAccountCardCodesAsync(cardCode);
            var allPayments = new List<CustomerPaymentSummary>();

            foreach (var mainCardCode in mainCardCodes)
            {
                var response = await _httpClient.GetFromJsonAsync<IncomingPaymentDateResponse>(
                    $"api/incomingpayment/customer/{mainCardCode}");
                var payments = response?.Payments ?? new List<IncomingPaymentDto>();

                var mapped = payments
                    .Select(p => new CustomerPaymentSummary
                    {
                        DocEntry = p.DocEntry,
                        DocNum = p.DocNum,
                        DocDate = ParseDate(p.DocDate),
                        DocTotal = p.DocTotal,
                        PaymentMethod = DeterminePaymentMethod(p),
                        Reference = p.TransferReference ?? p.Remarks,
                        Currency = p.DocCurrency
                    });

                allPayments.AddRange(mapped);
            }

            IEnumerable<CustomerPaymentSummary> result = allPayments;

            if (fromDate.HasValue)
                result = result.Where(p => p.DocDate >= fromDate.Value);

            if (toDate.HasValue)
                result = result.Where(p => p.DocDate <= toDate.Value);

            return result.OrderByDescending(p => p.DocDate).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payment history for {CardCode}", cardCode);
            return new List<CustomerPaymentSummary>();
        }
    }

    /// <summary>
    /// Get aging summary (current, 30, 60, 90, 90+ days)
    /// </summary>
    public async Task<AgingSummary> GetAgingSummaryAsync(string cardCode)
    {
        try
        {
            var openInvoices = await GetOpenInvoicesAsync(cardCode);
            var today = DateTime.Today;

            var aging = new AgingSummary();

            foreach (var invoice in openInvoices)
            {
                var daysOverdue = invoice.DaysOverdue;

                if (daysOverdue <= 0)
                    aging.Current += invoice.Balance;
                else if (daysOverdue <= 30)
                    aging.Days1To30 += invoice.Balance;
                else if (daysOverdue <= 60)
                    aging.Days31To60 += invoice.Balance;
                else if (daysOverdue <= 90)
                    aging.Days61To90 += invoice.Balance;
                else
                    aging.Over90Days += invoice.Balance;
            }

            aging.Total = aging.Current + aging.Days1To30 + aging.Days31To60 + aging.Days61To90 + aging.Over90Days;

            return aging;
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
                .Where(i =>
                    (includeClosedInvoices || (i.DocStatus != "C" && i.DocStatus != "X")) &&
                    ParseDate(i.DocDate) >= fromDate &&
                    ParseDate(i.DocDate) <= toDate)
                .Select(i => new CustomerInvoiceSummary
                {
                    DocEntry = i.DocEntry,
                    DocNum = i.DocNum,
                    CardCode = cardCode,
                    CardName = i.CardName,
                    DocDate = ParseDate(i.DocDate),
                    DueDate = ParseNullableDate(i.DocDueDate),
                    DocTotal = i.DocTotal,
                    PaidToDate = i.PaidToDate,
                    Balance = i.DocTotal - i.PaidToDate,
                    Currency = i.DocCurrency,
                    Status = GetInvoiceStatus(i.DocStatus),
                    DaysOverdue = CalculateDaysOverdue(i.DocDueDate)
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

    private async Task<List<CustomerPaymentSummary>> GetPaymentsForPeriodAsync(
        string cardCode, DateTime fromDate, DateTime toDate)
    {
        return await GetPaymentHistoryAsync(cardCode, fromDate, toDate);
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
            var mainCardCodes = await _linkedAccountService.GetMainAccountCardCodesAsync(cardCode);
            var now = IAuditService.ToCAT(DateTime.UtcNow);
            var fromDate = new DateTime(now.Year, now.Month, 1).AddMonths(-(months - 1));
            var toDate = now;

            var allInvoices = new List<CustomerInvoiceSummary>();
            foreach (var mainCardCode in mainCardCodes)
            {
                var response = await _invoiceService.GetInvoicesByCustomerAsync(mainCardCode, fromDate, toDate);
                if (response?.Invoices != null)
                {
                    allInvoices.AddRange(response.Invoices.Select(inv => new CustomerInvoiceSummary
                    {
                        DocDate = DateTime.TryParse(inv.DocDate?.ToString(), out var d) ? d : DateTime.MinValue,
                        DocTotal = inv.DocTotal
                    }));
                }
            }

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