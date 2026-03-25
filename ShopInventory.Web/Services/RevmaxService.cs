using System.Net.Http.Json;
using System.Text.Json;

namespace ShopInventory.Web.Services;

public interface IRevmaxService
{
    Task<RevmaxCardDetailsResponse?> GetCardDetailsAsync();
    Task<RevmaxDayStatusResponse?> GetDayStatusAsync();
    Task<RevmaxLicenseResponse?> GetLicenseAsync();
    Task<RevmaxLicenseResponse?> SetLicenseAsync(string license);
    Task<RevmaxZReportResponse?> GetZReportAsync();
    Task<RevmaxInvoiceResponse?> GetInvoiceAsync(string invoiceNumber);
    Task<RevmaxUnprocessedSummaryResponse?> GetUnprocessedInvoicesSummaryAsync();
}

public class RevmaxService : IRevmaxService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RevmaxService> _logger;

    public RevmaxService(HttpClient httpClient, ILogger<RevmaxService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<RevmaxCardDetailsResponse?> GetCardDetailsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<RevmaxCardDetailsResponse>("api/revmax/card-details");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get REVMax card details");
            return null;
        }
    }

    public async Task<RevmaxDayStatusResponse?> GetDayStatusAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<RevmaxDayStatusResponse>("api/revmax/day-status");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get REVMax day status");
            return null;
        }
    }

    public async Task<RevmaxLicenseResponse?> GetLicenseAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<RevmaxLicenseResponse>("api/revmax/license");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get REVMax license");
            return null;
        }
    }

    public async Task<RevmaxLicenseResponse?> SetLicenseAsync(string license)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/revmax/license", new { License = license });
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<RevmaxLicenseResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set REVMax license");
            return null;
        }
    }

    public async Task<RevmaxZReportResponse?> GetZReportAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<RevmaxZReportResponse>("api/revmax/z-report");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get REVMax Z-Report");
            return null;
        }
    }

    public async Task<RevmaxInvoiceResponse?> GetInvoiceAsync(string invoiceNumber)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<RevmaxInvoiceResponse>($"api/revmax/invoices/{Uri.EscapeDataString(invoiceNumber)}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new RevmaxInvoiceResponse { Code = "0", Message = "Invoice not found" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get REVMax invoice {InvoiceNumber}", invoiceNumber);
            return null;
        }
    }

    public async Task<RevmaxUnprocessedSummaryResponse?> GetUnprocessedInvoicesSummaryAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<RevmaxUnprocessedSummaryResponse>("api/revmax/unprocessed-invoices/summary");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get REVMax unprocessed invoices summary");
            return null;
        }
    }
}

// Response models for the web client
public class RevmaxCardDetailsResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? QRcode { get; set; }
    public string? VerificationCode { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? FiscalDay { get; set; }
    public RevmaxCardDetailsData? Data { get; set; }
}

public class RevmaxCardDetailsData
{
    public string? TIN { get; set; }
    public string? BPN { get; set; }
    public string? VAT { get; set; }
    public string? COMPANYNAME { get; set; }
    public string? ADDRESS { get; set; }
    public string? REGISTRATIONNUMBER { get; set; }
    public string? SERIALNUMBER { get; set; }
}

public class RevmaxDayStatusResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? FiscalDay { get; set; }
    public RevmaxDayStatusData? Data { get; set; }
}

public class RevmaxDayStatusData
{
    public string? FiscalDayStatus { get; set; }
    public int LastReceiptGlobalNo { get; set; }
    public int LastFiscalDayNo { get; set; }
    public string? OperationID { get; set; }
}

public class RevmaxLicenseResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public RevmaxLicenseData? Data { get; set; }
}

public class RevmaxLicenseData
{
    public string? Status { get; set; }
    public string? Start { get; set; }
    public string? End { get; set; }
}

public class RevmaxZReportResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? FiscalDay { get; set; }
    public object? Data { get; set; }
}

public class RevmaxInvoiceResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? QRcode { get; set; }
    public string? VerificationCode { get; set; }
    public string? VerificationLink { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? FiscalDay { get; set; }
    public RevmaxInvoiceData? Data { get; set; }
    public bool Success => Code == "1";
}

public class RevmaxInvoiceData
{
    public string? ReceiptType { get; set; }
    public string? ReceiptCurrency { get; set; }
    public int ReceiptCounter { get; set; }
    public long ReceiptGlobalNo { get; set; }
    public string? InvoiceNo { get; set; }
    public string? BuyerData { get; set; }
    public string? ReceiptNotes { get; set; }
    public string? ReceiptDate { get; set; }
    public string? CreditDebitNote { get; set; }
    public bool ReceiptLinesTaxInclusive { get; set; }
    public List<RevmaxReceiptLine>? ReceiptLines { get; set; }
    public List<RevmaxReceiptTax>? ReceiptTaxes { get; set; }
    public List<RevmaxReceiptPayment>? ReceiptPayments { get; set; }
    public decimal ReceiptTotal { get; set; }
    public string? ReceiptPrintForm { get; set; }
}

public class RevmaxReceiptLine
{
    public string? ReceiptLineName { get; set; }
    public int ReceiptLineNo { get; set; }
    public decimal ReceiptLineQuantity { get; set; }
    public string? ReceiptLineType { get; set; }
    public decimal ReceiptLineTotal { get; set; }
    public int TaxID { get; set; }
    public string? ReceiptLineHSCode { get; set; }
    public decimal ReceiptLinePrice { get; set; }
    public string? TaxCode { get; set; }
    public decimal TaxPercent { get; set; }
}

public class RevmaxReceiptTax
{
    public decimal SalesAmountWithTax { get; set; }
    public decimal TaxAmount { get; set; }
    public int TaxID { get; set; }
    public string? TaxCode { get; set; }
    public decimal TaxPercent { get; set; }
}

public class RevmaxReceiptPayment
{
    public string? MoneyTypeCode { get; set; }
    public decimal PaymentAmount { get; set; }
}

public class RevmaxUnprocessedSummaryResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public List<RevmaxUnprocessedInvoice>? Data { get; set; }
}

public class RevmaxUnprocessedInvoice
{
    public string? InvoiceNumber { get; set; }
    public decimal InvoiceAmount { get; set; }
    public string? Currency { get; set; }
    public string? Status { get; set; }
    public string? CreatedDate { get; set; }
}
