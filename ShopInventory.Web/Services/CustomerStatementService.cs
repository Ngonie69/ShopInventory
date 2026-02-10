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
/// Customer statement service for portal functionality
/// </summary>
public class CustomerStatementService : ICustomerStatementService
{
    private readonly HttpClient _httpClient;
    private readonly IBusinessPartnerService _businessPartnerService;
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<CustomerStatementService> _logger;

    public CustomerStatementService(
        HttpClient httpClient,
        IBusinessPartnerService businessPartnerService,
        IInvoiceService invoiceService,
        ILogger<CustomerStatementService> logger)
    {
        _httpClient = httpClient;
        _businessPartnerService = businessPartnerService;
        _invoiceService = invoiceService;
        _logger = logger;
    }

    /// <summary>
    /// Get customer statement with transaction details
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

            var response = new CustomerStatementResponse
            {
                Customer = new CustomerInfo
                {
                    CardCode = customer.CardCode ?? cardCode,
                    CardName = customer.CardName ?? "",
                    Email = customer.Email,
                    Phone = customer.Phone1,
                    Balance = customer.Balance ?? 0,
                    Currency = customer.Currency
                },
                FromDate = request.FromDate,
                ToDate = request.ToDate,
                GeneratedAt = DateTime.UtcNow,
                Lines = new List<StatementLine>()
            };

            // Get invoices
            var invoices = await GetInvoicesForPeriodAsync(cardCode, request.FromDate, request.ToDate, request.IncludeClosedInvoices);

            // Get payments
            var payments = await GetPaymentsForPeriodAsync(cardCode, request.FromDate, request.ToDate);

            // Combine and sort transactions
            var allTransactions = new List<(DateTime Date, string Type, StatementLine Line)>();

            foreach (var invoice in invoices)
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

            foreach (var payment in payments)
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

            // Get aging summary
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
    /// Get customer dashboard summary
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

            var summary = new CustomerDashboardSummary
            {
                Customer = new CustomerInfo
                {
                    CardCode = customer.CardCode ?? cardCode,
                    CardName = customer.CardName ?? "",
                    Email = customer.Email,
                    Phone = customer.Phone1,
                    Balance = customer.Balance ?? 0,
                    Currency = customer.Currency
                },
                AccountBalance = customer.Balance ?? 0
            };

            // Get open invoices
            var openInvoices = await GetOpenInvoicesAsync(cardCode);
            summary.OpenInvoicesCount = openInvoices.Count;
            summary.TotalOutstanding = openInvoices.Sum(i => i.Balance);
            summary.OverdueInvoicesCount = openInvoices.Count(i => i.DaysOverdue > 0);
            summary.OverdueAmount = openInvoices.Where(i => i.DaysOverdue > 0).Sum(i => i.Balance);
            summary.RecentInvoices = openInvoices.Take(5).ToList();

            // Get recent payments
            var payments = await GetPaymentHistoryAsync(cardCode, DateTime.Now.AddMonths(-3), DateTime.Now);
            if (payments.Any())
            {
                var lastPayment = payments.OrderByDescending(p => p.DocDate).First();
                summary.LastPaymentDate = lastPayment.DocDate;
                summary.LastPaymentAmount = lastPayment.DocTotal;
            }
            summary.RecentPayments = payments.Take(5).ToList();

            // Get aging summary
            summary.Aging = await GetAgingSummaryAsync(cardCode);

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dashboard summary for {CardCode}", cardCode);
            throw;
        }
    }

    /// <summary>
    /// Get open (unpaid) invoices for customer
    /// </summary>
    public async Task<List<CustomerInvoiceSummary>> GetOpenInvoicesAsync(string cardCode)
    {
        try
        {
            var invoiceResponse = await _invoiceService.GetInvoicesByCustomerAsync(cardCode);
            var invoices = invoiceResponse?.Invoices ?? new List<InvoiceDto>();

            return invoices
                .Where(i => i.DocStatus != "C" && i.DocStatus != "X") // Not closed or cancelled
                .Select(i => new CustomerInvoiceSummary
                {
                    DocEntry = i.DocEntry,
                    DocNum = i.DocNum,
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
            _logger.LogError(ex, "Error getting open invoices for {CardCode}", cardCode);
            return new List<CustomerInvoiceSummary>();
        }
    }

    /// <summary>
    /// Get payment history for customer
    /// </summary>
    public async Task<List<CustomerPaymentSummary>> GetPaymentHistoryAsync(string cardCode, DateTime? fromDate, DateTime? toDate)
    {
        try
        {
            // Get payments from API
            var response = await _httpClient.GetFromJsonAsync<IncomingPaymentDateResponse>($"api/incomingpayment/customer/{cardCode}");
            var payments = response?.Payments ?? new List<IncomingPaymentDto>();

            var result = payments
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