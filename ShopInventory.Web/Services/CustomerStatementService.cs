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
    Task<List<CustomerInvoiceSummary>> GetOpenInvoicesAsync(string cardCode);
    Task<List<CustomerPaymentSummary>> GetPaymentHistoryAsync(string cardCode, DateTime? fromDate, DateTime? toDate);
    Task<AgingSummary> GetAgingSummaryAsync(string cardCode);
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
    private readonly ILogger<CustomerStatementService> _logger;

    public CustomerStatementService(
        HttpClient httpClient,
        IBusinessPartnerService businessPartnerService,
        IInvoiceService invoiceService,
        ICustomerLinkedAccountService linkedAccountService,
        ISalesOrderService salesOrderService,
        ILogger<CustomerStatementService> logger)
    {
        _httpClient = httpClient;
        _businessPartnerService = businessPartnerService;
        _invoiceService = invoiceService;
        _linkedAccountService = linkedAccountService;
        _salesOrderService = salesOrderService;
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
            var customer = await _businessPartnerService.GetBusinessPartnerByCodeAsync(cardCode);
            if (customer == null)
            {
                throw new InvalidOperationException("Customer not found");
            }

            var accountStructure = await _linkedAccountService.GetAccountStructureAsync(cardCode);
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

            // Get open invoices (aggregated from all main accounts for multi-account)
            var openInvoices = await GetOpenInvoicesAsync(cardCode);
            summary.OpenInvoicesCount = openInvoices.Count;
            summary.TotalOutstanding = openInvoices.Sum(i => i.Balance);
            summary.OverdueInvoicesCount = openInvoices.Count(i => i.DaysOverdue > 0);
            summary.OverdueAmount = openInvoices.Where(i => i.DaysOverdue > 0).Sum(i => i.Balance);
            summary.RecentInvoices = openInvoices.Take(5).ToList();

            // Get recent payments (aggregated from all main accounts for multi-account)
            var payments = await GetPaymentHistoryAsync(cardCode, IAuditService.ToCAT(DateTime.UtcNow).AddMonths(-3), IAuditService.ToCAT(DateTime.UtcNow));
            if (payments.Any())
            {
                var lastPayment = payments.OrderByDescending(p => p.DocDate).First();
                summary.LastPaymentDate = lastPayment.DocDate;
                summary.LastPaymentAmount = lastPayment.DocTotal;
            }
            summary.RecentPayments = payments.Take(5).ToList();

            // Get aging summary (aggregated)
            summary.Aging = await GetAgingSummaryAsync(cardCode);

            // Build per-account breakdown for multi-account customers
            if (accountStructure == "Multi" && linkedAccounts.Any())
            {
                summary.AccountBreakdown = await BuildAccountBreakdownAsync(linkedAccounts);

                // Sum balances across all main accounts for the total
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
    /// Build per-account breakdown showing individual balances and transaction counts
    /// </summary>
    private async Task<List<AccountSummary>> BuildAccountBreakdownAsync(List<LinkedAccountInfo> linkedAccounts)
    {
        var breakdown = new List<AccountSummary>();

        foreach (var account in linkedAccounts)
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
                    // Main accounts: show invoices
                    var invoices = await GetOpenInvoicesForSingleAccountAsync(account.CardCode);
                    acctSummary.OpenInvoicesCount = invoices.Count;
                    acctSummary.TotalOutstanding = invoices.Sum(i => i.Balance);
                }
                else if (account.AccountType == "Sub")
                {
                    // Sub accounts: show sales orders
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

            breakdown.Add(acctSummary);
        }

        return breakdown;
    }

    /// <summary>
    /// Get open (unpaid) invoices for customer.
    /// For multi-account customers, aggregates invoices from all main accounts.
    /// </summary>
    public async Task<List<CustomerInvoiceSummary>> GetOpenInvoicesAsync(string cardCode)
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

            return allInvoices
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

    private static string DeterminePaymentMethod(IncomingPaymentDto payment)
    {
        if (payment.CashSum > 0) return "Cash";
        if (payment.CheckSum > 0) return "Check";
        if (payment.TransferSum > 0) return "Transfer";
        if (payment.CreditSum > 0) return "Credit Card";
        return "Other";
    }
    #endregion
}