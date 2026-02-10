using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Services;

/// <summary>
/// Service for fiscalizing invoices and credit notes with REVMax.
/// Invoices are only fiscalized after successful SAP posting.
/// </summary>
public interface IFiscalizationService
{
    /// <summary>
    /// Fiscalizes an invoice that has been successfully posted to SAP.
    /// </summary>
    /// <param name="invoice">The SAP invoice to fiscalize</param>
    /// <param name="customerDetails">Optional customer details for fiscalization</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Fiscalization result with QR code and fiscal details</returns>
    Task<FiscalizationResult> FiscalizeInvoiceAsync(
        InvoiceDto invoice,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fiscalizes a credit note that has been successfully posted to SAP.
    /// Requires the original invoice to be already fiscalized.
    /// </summary>
    /// <param name="creditNote">The SAP credit note to fiscalize</param>
    /// <param name="originalInvoiceNumber">The original invoice number (DocNum)</param>
    /// <param name="customerDetails">Optional customer details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Fiscalization result</returns>
    Task<FiscalizationResult> FiscalizeCreditNoteAsync(
        InvoiceDto creditNote,
        string originalInvoiceNumber,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an invoice has already been fiscalized.
    /// </summary>
    /// <param name="invoiceNumber">The invoice number to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if already fiscalized</returns>
    Task<bool> IsInvoiceFiscalizedAsync(string invoiceNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Customer fiscal details for REVMax.
/// </summary>
public class CustomerFiscalDetails
{
    public string? CustomerName { get; set; }
    public string? VatNumber { get; set; }
    public string? Address { get; set; }
    public string? Telephone { get; set; }
    public string? Email { get; set; }
    public string? BPN { get; set; }
}

/// <summary>
/// Result of a fiscalization operation.
/// </summary>
public class FiscalizationResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? QRCode { get; set; }
    public string? FiscalDayNo { get; set; }
    public string? ReceiptGlobalNo { get; set; }
    public string? ReceiptCounter { get; set; }
    public string? DeviceSerial { get; set; }
    public string? VerificationCode { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetails { get; set; }

    /// <summary>
    /// Invoice number that was fiscalized.
    /// </summary>
    public string? InvoiceNumber { get; set; }

    /// <summary>
    /// Whether fiscalization was skipped (e.g., REVMax not configured).
    /// </summary>
    public bool Skipped { get; set; }
}

/// <summary>
/// Implementation of fiscalization service using REVMax.
/// </summary>
public class FiscalizationService : IFiscalizationService
{
    private readonly IRevmaxClient _revmaxClient;
    private readonly RevmaxSettings _settings;
    private readonly ILogger<FiscalizationService> _logger;

    public FiscalizationService(
        IRevmaxClient revmaxClient,
        IOptions<RevmaxSettings> settings,
        ILogger<FiscalizationService> logger)
    {
        _revmaxClient = revmaxClient ?? throw new ArgumentNullException(nameof(revmaxClient));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<FiscalizationResult> FiscalizeInvoiceAsync(
        InvoiceDto invoice,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default)
    {
        if (invoice == null)
        {
            throw new ArgumentNullException(nameof(invoice));
        }

        var invoiceNumber = invoice.DocNum.ToString();

        try
        {
            _logger.LogInformation(
                "Starting fiscalization for invoice {InvoiceNumber} (DocEntry: {DocEntry})",
                invoiceNumber, invoice.DocEntry);

            // Check if already fiscalized to prevent duplicates
            if (await IsInvoiceFiscalizedAsync(invoiceNumber, cancellationToken))
            {
                _logger.LogWarning(
                    "Invoice {InvoiceNumber} is already fiscalized - skipping",
                    invoiceNumber);

                return new FiscalizationResult
                {
                    Success = true,
                    Skipped = true,
                    Message = $"Invoice {invoiceNumber} is already fiscalized",
                    InvoiceNumber = invoiceNumber
                };
            }

            // Build the TransactM request
            var request = BuildTransactMRequest(invoice, customerDetails);

            // Post to REVMax
            var response = await _revmaxClient.TransactMAsync(request, cancellationToken);

            if (response == null)
            {
                _logger.LogError("REVMax returned null response for invoice {InvoiceNumber}", invoiceNumber);

                return new FiscalizationResult
                {
                    Success = false,
                    Message = "REVMax returned no response",
                    InvoiceNumber = invoiceNumber,
                    ErrorCode = "NO_RESPONSE"
                };
            }

            if (response.Success)
            {
                _logger.LogInformation(
                    "Invoice {InvoiceNumber} fiscalized successfully. ReceiptGlobalNo: {ReceiptGlobalNo}, FiscalDayNo: {FiscalDayNo}",
                    invoiceNumber, response.ReceiptGlobalNo, response.FiscalDayNo);

                return new FiscalizationResult
                {
                    Success = true,
                    Message = response.Message ?? "Fiscalization successful",
                    InvoiceNumber = invoiceNumber,
                    QRCode = response.QRcode,
                    FiscalDayNo = response.FiscalDayNo,
                    ReceiptGlobalNo = response.ReceiptGlobalNo,
                    ReceiptCounter = response.ReceiptCounter,
                    DeviceSerial = response.DeviceSerial,
                    VerificationCode = response.VerificationCode
                };
            }
            else
            {
                _logger.LogWarning(
                    "Fiscalization failed for invoice {InvoiceNumber}: {Message}",
                    invoiceNumber, response.Message);

                return new FiscalizationResult
                {
                    Success = false,
                    Message = response.Message ?? "Fiscalization failed",
                    InvoiceNumber = invoiceNumber,
                    ErrorCode = "REVMAX_ERROR"
                };
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "HTTP error during fiscalization of invoice {InvoiceNumber}",
                invoiceNumber);

            return new FiscalizationResult
            {
                Success = false,
                Message = "Failed to connect to REVMax fiscal device",
                InvoiceNumber = invoiceNumber,
                ErrorCode = "CONNECTION_ERROR",
                ErrorDetails = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error during fiscalization of invoice {InvoiceNumber}",
                invoiceNumber);

            return new FiscalizationResult
            {
                Success = false,
                Message = "Fiscalization error",
                InvoiceNumber = invoiceNumber,
                ErrorCode = "UNEXPECTED_ERROR",
                ErrorDetails = ex.Message
            };
        }
    }

    public async Task<FiscalizationResult> FiscalizeCreditNoteAsync(
        InvoiceDto creditNote,
        string originalInvoiceNumber,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default)
    {
        if (creditNote == null)
        {
            throw new ArgumentNullException(nameof(creditNote));
        }

        if (string.IsNullOrWhiteSpace(originalInvoiceNumber))
        {
            throw new ArgumentException("Original invoice number is required for credit notes", nameof(originalInvoiceNumber));
        }

        var creditNoteNumber = creditNote.DocNum.ToString();

        try
        {
            _logger.LogInformation(
                "Starting fiscalization for credit note {CreditNoteNumber} (Original: {OriginalInvoice})",
                creditNoteNumber, originalInvoiceNumber);

            // Check if credit note already fiscalized
            if (await IsInvoiceFiscalizedAsync(creditNoteNumber, cancellationToken))
            {
                _logger.LogWarning(
                    "Credit note {CreditNoteNumber} is already fiscalized - skipping",
                    creditNoteNumber);

                return new FiscalizationResult
                {
                    Success = true,
                    Skipped = true,
                    Message = $"Credit note {creditNoteNumber} is already fiscalized",
                    InvoiceNumber = creditNoteNumber
                };
            }

            // Verify original invoice is fiscalized
            var originalInvoice = await _revmaxClient.GetInvoiceAsync(originalInvoiceNumber, cancellationToken);

            bool originalFiscalized = originalInvoice != null &&
                                      originalInvoice.Success &&
                                      (!string.IsNullOrWhiteSpace(originalInvoice.QRcode) ||
                                       (originalInvoice.Data?.ReceiptGlobalNo > 0));

            if (!originalFiscalized)
            {
                _logger.LogError(
                    "Cannot fiscalize credit note {CreditNoteNumber}: Original invoice {OriginalInvoice} is not fiscalized",
                    creditNoteNumber, originalInvoiceNumber);

                return new FiscalizationResult
                {
                    Success = false,
                    Message = $"Original invoice {originalInvoiceNumber} must be fiscalized before the credit note",
                    InvoiceNumber = creditNoteNumber,
                    ErrorCode = "ORIGINAL_NOT_FISCALIZED"
                };
            }

            // Build the TransactMExt request for credit note
            var request = BuildCreditNoteRequest(creditNote, originalInvoiceNumber, originalInvoice, customerDetails);

            // Post to REVMax
            var response = await _revmaxClient.TransactMExtAsync(request, cancellationToken);

            if (response == null)
            {
                _logger.LogError("REVMax returned null response for credit note {CreditNoteNumber}", creditNoteNumber);

                return new FiscalizationResult
                {
                    Success = false,
                    Message = "REVMax returned no response",
                    InvoiceNumber = creditNoteNumber,
                    ErrorCode = "NO_RESPONSE"
                };
            }

            if (response.Success)
            {
                _logger.LogInformation(
                    "Credit note {CreditNoteNumber} fiscalized successfully. ReceiptGlobalNo: {ReceiptGlobalNo}",
                    creditNoteNumber, response.ReceiptGlobalNo);

                return new FiscalizationResult
                {
                    Success = true,
                    Message = response.Message ?? "Credit note fiscalization successful",
                    InvoiceNumber = creditNoteNumber,
                    QRCode = response.QRcode,
                    FiscalDayNo = response.FiscalDayNo,
                    ReceiptGlobalNo = response.ReceiptGlobalNo,
                    ReceiptCounter = response.ReceiptCounter,
                    DeviceSerial = response.DeviceSerial,
                    VerificationCode = response.VerificationCode
                };
            }
            else
            {
                _logger.LogWarning(
                    "Credit note fiscalization failed for {CreditNoteNumber}: {Message}",
                    creditNoteNumber, response.Message);

                return new FiscalizationResult
                {
                    Success = false,
                    Message = response.Message ?? "Credit note fiscalization failed",
                    InvoiceNumber = creditNoteNumber,
                    ErrorCode = "REVMAX_ERROR"
                };
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "HTTP error during credit note fiscalization {CreditNoteNumber}",
                creditNoteNumber);

            return new FiscalizationResult
            {
                Success = false,
                Message = "Failed to connect to REVMax fiscal device",
                InvoiceNumber = creditNoteNumber,
                ErrorCode = "CONNECTION_ERROR",
                ErrorDetails = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error during credit note fiscalization {CreditNoteNumber}",
                creditNoteNumber);

            return new FiscalizationResult
            {
                Success = false,
                Message = "Credit note fiscalization error",
                InvoiceNumber = creditNoteNumber,
                ErrorCode = "UNEXPECTED_ERROR",
                ErrorDetails = ex.Message
            };
        }
    }

    public async Task<bool> IsInvoiceFiscalizedAsync(string invoiceNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            var invoice = await _revmaxClient.GetInvoiceAsync(invoiceNumber, cancellationToken);

            if (invoice == null || !invoice.Success)
            {
                return false;
            }

            // Check for fiscal evidence
            return !string.IsNullOrWhiteSpace(invoice.QRcode) ||
                   (invoice.Data?.ReceiptGlobalNo > 0);
        }
        catch (HttpRequestException)
        {
            // Invoice not found on REVMax - not fiscalized
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking fiscalization status for {InvoiceNumber}", invoiceNumber);
            return false;
        }
    }

    private TransactMRequest BuildTransactMRequest(InvoiceDto invoice, CustomerFiscalDetails? customerDetails)
    {
        var itemsXml = BuildItemsXml(invoice);
        var currenciesXml = BuildCurrenciesXml(invoice);

        return new TransactMRequest
        {
            InvoiceNumber = invoice.DocNum.ToString(),
            Currency = invoice.DocCurrency ?? _settings.DefaultCurrency,
            BranchName = _settings.DefaultBranchName,
            CustomerName = customerDetails?.CustomerName ?? invoice.CardName,
            CustomerVatNumber = customerDetails?.VatNumber,
            CustomerAddress = customerDetails?.Address,
            CustomerTelephone = customerDetails?.Telephone,
            CustomerEmail = customerDetails?.Email,
            CustomerBPN = customerDetails?.BPN,
            InvoiceAmount = invoice.DocTotal,
            InvoiceTaxAmount = invoice.VatSum,
            Istatus = "01", // Normal invoice
            Cashier = "System",
            InvoiceComment = invoice.Comments,
            ItemsXml = itemsXml,
            CurrenciesXml = currenciesXml
        };
    }

    private TransactMExtRequest BuildCreditNoteRequest(
        InvoiceDto creditNote,
        string originalInvoiceNumber,
        InvoiceResponse? originalInvoice,
        CustomerFiscalDetails? customerDetails)
    {
        var itemsXml = BuildItemsXml(creditNote);
        var currenciesXml = BuildCurrenciesXml(creditNote);

        return new TransactMExtRequest
        {
            InvoiceNumber = creditNote.DocNum.ToString(),
            OriginalInvoiceNumber = originalInvoiceNumber,
            Currency = creditNote.DocCurrency ?? _settings.DefaultCurrency,
            BranchName = _settings.DefaultBranchName,
            CustomerName = customerDetails?.CustomerName ?? creditNote.CardName,
            CustomerVatNumber = customerDetails?.VatNumber,
            CustomerAddress = customerDetails?.Address,
            CustomerTelephone = customerDetails?.Telephone,
            CustomerEmail = customerDetails?.Email,
            CustomerBPN = customerDetails?.BPN,
            InvoiceAmount = Math.Abs(creditNote.DocTotal), // Credit notes often have negative amounts
            InvoiceTaxAmount = Math.Abs(creditNote.VatSum),
            Istatus = "02", // Credit note
            Cashier = "System",
            InvoiceComment = creditNote.Comments ?? creditNote.Remarks,
            ItemsXml = itemsXml,
            CurrenciesXml = currenciesXml,
            refDeviceId = _settings.DefaultRefDeviceId,
            refFiscalDayNo = originalInvoice?.Data?.FiscalDayNo,
            refReceiptGlobalNo = originalInvoice?.Data?.ReceiptGlobalNo
        };
    }

    private string BuildItemsXml(InvoiceDto invoice)
    {
        var items = new XElement("items");

        if (invoice.Lines == null || invoice.Lines.Count == 0)
        {
            return items.ToString(SaveOptions.DisableFormatting);
        }

        int lineNum = 1;
        foreach (var line in invoice.Lines)
        {
            var qty = Math.Abs(line.Quantity);
            var price = line.UnitPrice;
            var amt = qty * price; // CRITICAL: Calculate AMT = QTY × PRICE

            // Determine TAXR - use configured VAT rate (15.5%)
            var taxRate = _settings.VatRate;

            var item = new XElement("item",
                new XElement("HH", lineNum.ToString()),
                new XElement("ITEMCODE", line.ItemCode ?? ""),
                new XElement("ITEMNAME1", line.ItemDescription ?? ""),
                new XElement("ITEMNAME2", ""),
                new XElement("QTY", qty.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("PRICE", price.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("AMT", amt.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("TAX", "0"),
                new XElement("TAXR", taxRate.ToString("F4", CultureInfo.InvariantCulture))
            );

            items.Add(item);
            lineNum++;
        }

        return items.ToString(SaveOptions.DisableFormatting);
    }

    private string BuildCurrenciesXml(InvoiceDto invoice)
    {
        var currencies = new XElement("currencies",
            new XElement("currency",
                new XElement("Name", invoice.DocCurrency ?? _settings.DefaultCurrency),
                new XElement("Amount", invoice.DocTotal.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("Rate", "1.00")
            )
        );

        return currencies.ToString(SaveOptions.DisableFormatting);
    }
}
