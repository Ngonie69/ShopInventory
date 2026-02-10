using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// REVMax Fiscal Integration Controller.
/// Handles fiscal transactions with REVMax API including invoices, credit notes, and reporting.
/// Enforces VAT at 15.5% effective 1 January 2026.
/// </summary>
[ApiController]
[Route("api/revmax")]
[Authorize(Policy = "RequireApiKey")]
public class RevmaxProxyController : ControllerBase
{
    private readonly IRevmaxClient _revmaxClient;
    private readonly RevmaxSettings _settings;
    private readonly ILogger<RevmaxProxyController> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public RevmaxProxyController(
        IRevmaxClient revmaxClient,
        IOptions<RevmaxSettings> settings,
        ILogger<RevmaxProxyController> logger)
    {
        _revmaxClient = revmaxClient ?? throw new ArgumentNullException(nameof(revmaxClient));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Gets fiscal device card details.
    /// </summary>
    [HttpGet("card-details")]
    [ProducesResponseType(typeof(CardDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCardDetails(CancellationToken cancellationToken)
    {
        var correlationId = GetCorrelationId();

        try
        {
            _logger.LogInformation("Getting card details. CorrelationId: {CorrelationId}", correlationId);

            var result = await _revmaxClient.GetCardDetailsAsync(cancellationToken);

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            return CreateUpstreamErrorResponse(ex, "GetCardDetails", correlationId);
        }
    }

    /// <summary>
    /// Gets current fiscal day status.
    /// </summary>
    [HttpGet("day-status")]
    [ProducesResponseType(typeof(DayStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetDayStatus(CancellationToken cancellationToken)
    {
        var correlationId = GetCorrelationId();

        try
        {
            _logger.LogInformation("Getting day status. CorrelationId: {CorrelationId}", correlationId);

            var result = await _revmaxClient.GetDayStatusAsync(cancellationToken);

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            return CreateUpstreamErrorResponse(ex, "GetDayStatus", correlationId);
        }
    }

    /// <summary>
    /// Gets current license information.
    /// </summary>
    [HttpGet("license")]
    [ProducesResponseType(typeof(LicenseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetLicense(CancellationToken cancellationToken)
    {
        var correlationId = GetCorrelationId();

        try
        {
            _logger.LogInformation("Getting license. CorrelationId: {CorrelationId}", correlationId);

            var result = await _revmaxClient.GetLicenseAsync(cancellationToken);

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            return CreateUpstreamErrorResponse(ex, "GetLicense", correlationId);
        }
    }

    /// <summary>
    /// Sets a new license.
    /// </summary>
    [HttpPost("license")]
    [ProducesResponseType(typeof(LicenseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SetLicense([FromBody] SetLicenseRequest? request, CancellationToken cancellationToken)
    {
        var correlationId = GetCorrelationId();

        if (request == null)
        {
            return CreateValidationError("Request body is required");
        }

        if (string.IsNullOrWhiteSpace(request.License))
        {
            ModelState.AddModelError(nameof(request.License), "License is required");
            return ValidationProblem(ModelState);
        }

        try
        {
            _logger.LogInformation("Setting license. CorrelationId: {CorrelationId}", correlationId);

            var result = await _revmaxClient.SetLicenseAsync(request.License, cancellationToken);

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            return CreateUpstreamErrorResponse(ex, "SetLicense", correlationId);
        }
    }

    /// <summary>
    /// Generates Z-Report (end of fiscal day report).
    /// </summary>
    [HttpGet("z-report")]
    [ProducesResponseType(typeof(ZReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetZReport(CancellationToken cancellationToken)
    {
        var correlationId = GetCorrelationId();

        try
        {
            _logger.LogInformation("Generating Z-Report. CorrelationId: {CorrelationId}", correlationId);

            var result = await _revmaxClient.GetZReportAsync(cancellationToken);

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            return CreateUpstreamErrorResponse(ex, "ZReport", correlationId);
        }
    }

    /// <summary>
    /// Gets invoice details by invoice number.
    /// </summary>
    [HttpGet("invoices/{invoiceNumber}")]
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInvoice([FromRoute] string invoiceNumber, CancellationToken cancellationToken)
    {
        var correlationId = GetCorrelationId();

        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            ModelState.AddModelError(nameof(invoiceNumber), "Invoice number is required");
            return ValidationProblem(ModelState);
        }

        try
        {
            _logger.LogInformation("Getting invoice {InvoiceNumber}. CorrelationId: {CorrelationId}",
                invoiceNumber, correlationId);

            var result = await _revmaxClient.GetInvoiceAsync(invoiceNumber, cancellationToken);

            if (result == null || !result.Success)
            {
                return NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Invoice Not Found",
                    Detail = $"Invoice '{invoiceNumber}' not found on REVMax",
                    Instance = HttpContext.Request.Path,
                    Extensions = { ["correlationId"] = correlationId }
                });
            }

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            return CreateUpstreamErrorResponse(ex, "GetInvoice", correlationId);
        }
    }

    /// <summary>
    /// Gets summary of unprocessed invoices.
    /// </summary>
    [HttpGet("unprocessed-invoices/summary")]
    [ProducesResponseType(typeof(UnprocessedInvoicesSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetUnprocessedInvoicesSummary(CancellationToken cancellationToken)
    {
        var correlationId = GetCorrelationId();

        try
        {
            _logger.LogInformation("Getting unprocessed invoices summary. CorrelationId: {CorrelationId}", correlationId);

            var result = await _revmaxClient.GetUnprocessedInvoicesSummaryAsync(cancellationToken);

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            return CreateUpstreamErrorResponse(ex, "GetUnprocessedInvoicesSummary", correlationId);
        }
    }

    /// <summary>
    /// Posts a fiscal transaction (invoice or credit note).
    /// Enforces credit note validation and duplicate prevention.
    /// VAT Rate: 15.5% effective 1 January 2026.
    /// </summary>
    [HttpPost("transact")]
    [ProducesResponseType(typeof(TransactMResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Transact([FromBody] TransactMRequest? request, CancellationToken cancellationToken)
    {
        var correlationId = GetCorrelationId();

        // Validate request
        var validationResult = ValidateTransactRequest(request);
        if (validationResult != null)
        {
            return validationResult;
        }

        try
        {
            // Apply defaults
            ApplyDefaults(request!);

            // Process and validate items
            var itemsResult = ProcessAndValidateItems(request!);
            if (itemsResult.Error != null)
            {
                return itemsResult.Error;
            }
            request!.ItemsXml = itemsResult.ItemsXml;

            // Check if this is a credit note
            bool isCreditNote = !string.IsNullOrWhiteSpace(request.OriginalInvoiceNumber);

            if (isCreditNote)
            {
                // CREDIT NOTE FLOW

                // 1. Validate original invoice exists and is fiscalized
                var originalInvoiceResult = await ValidateOriginalInvoiceAsync(
                    request.OriginalInvoiceNumber!, correlationId, cancellationToken);

                if (originalInvoiceResult.Error != null)
                {
                    return originalInvoiceResult.Error;
                }

                // 2. Check for duplicate credit note fiscalization
                var duplicateResult = await CheckDuplicateFiscalizationAsync(
                    request.InvoiceNumber!, correlationId, cancellationToken);

                if (duplicateResult.Error != null)
                {
                    return duplicateResult.Error;
                }

                // 3. Set credit note status
                request.Istatus = "02";

                _logger.LogInformation(
                    "Processing credit note {CreditNoteNumber} for original invoice {OriginalInvoiceNumber}. CorrelationId: {CorrelationId}",
                    request.InvoiceNumber, request.OriginalInvoiceNumber, correlationId);
            }
            else
            {
                _logger.LogInformation(
                    "Processing invoice {InvoiceNumber}. CorrelationId: {CorrelationId}",
                    request.InvoiceNumber, correlationId);
            }

            // Post transaction
            var result = await _revmaxClient.TransactMAsync(request, cancellationToken);

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            return CreateUpstreamErrorResponse(ex, "TransactM", correlationId);
        }
    }

    /// <summary>
    /// Posts a fiscal transaction with extended reference fields.
    /// Enforces credit note validation and duplicate prevention.
    /// VAT Rate: 15.5% effective 1 January 2026.
    /// </summary>
    [HttpPost("transact-ext")]
    [ProducesResponseType(typeof(TransactMExtResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> TransactExt([FromBody] TransactMExtRequest? request, CancellationToken cancellationToken)
    {
        var correlationId = GetCorrelationId();

        // Validate request
        var validationResult = ValidateTransactRequest(request);
        if (validationResult != null)
        {
            return validationResult;
        }

        try
        {
            // Apply defaults
            ApplyDefaults(request!);
            request!.refDeviceId ??= _settings.DefaultRefDeviceId;

            // Process and validate items
            var itemsResult = ProcessAndValidateItems(request);
            if (itemsResult.Error != null)
            {
                return itemsResult.Error;
            }
            request.ItemsXml = itemsResult.ItemsXml;

            // Check if this is a credit note
            bool isCreditNote = !string.IsNullOrWhiteSpace(request.OriginalInvoiceNumber);

            if (isCreditNote)
            {
                // CREDIT NOTE FLOW

                // 1. Validate original invoice exists and is fiscalized
                var originalInvoiceResult = await ValidateOriginalInvoiceAsync(
                    request.OriginalInvoiceNumber!, correlationId, cancellationToken);

                if (originalInvoiceResult.Error != null)
                {
                    return originalInvoiceResult.Error;
                }

                // Copy fiscal reference from original invoice if available
                if (originalInvoiceResult.Invoice?.Data != null)
                {
                    request.refFiscalDayNo ??= originalInvoiceResult.Invoice.Data.FiscalDayNo;
                    request.refReceiptGlobalNo ??= originalInvoiceResult.Invoice.Data.ReceiptGlobalNo;
                }

                // 2. Check for duplicate credit note fiscalization
                var duplicateResult = await CheckDuplicateFiscalizationAsync(
                    request.InvoiceNumber!, correlationId, cancellationToken);

                if (duplicateResult.Error != null)
                {
                    return duplicateResult.Error;
                }

                // 3. Set credit note status
                request.Istatus = "02";

                _logger.LogInformation(
                    "Processing credit note (ext) {CreditNoteNumber} for original invoice {OriginalInvoiceNumber}. CorrelationId: {CorrelationId}",
                    request.InvoiceNumber, request.OriginalInvoiceNumber, correlationId);
            }
            else
            {
                _logger.LogInformation(
                    "Processing invoice (ext) {InvoiceNumber}. CorrelationId: {CorrelationId}",
                    request.InvoiceNumber, correlationId);
            }

            // Post transaction
            var result = await _revmaxClient.TransactMExtAsync(request, cancellationToken);

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            return CreateUpstreamErrorResponse(ex, "TransactMExt", correlationId);
        }
    }

    #region Validation Methods

    private IActionResult? ValidateTransactRequest(TransactMRequest? request)
    {
        if (request == null)
        {
            return CreateValidationError("Request body is required");
        }

        var errors = new Dictionary<string, string[]>();

        // InvoiceNumber required
        if (string.IsNullOrWhiteSpace(request.InvoiceNumber))
        {
            errors[nameof(request.InvoiceNumber)] = ["Invoice number is required"];
        }

        // ItemsXml required
        if (string.IsNullOrWhiteSpace(request.ItemsXml))
        {
            errors[nameof(request.ItemsXml)] = ["Items XML is required and cannot be empty"];
        }

        // InvoiceAmount >= 0
        if (request.InvoiceAmount < 0)
        {
            errors[nameof(request.InvoiceAmount)] = ["Invoice amount must be >= 0"];
        }

        // InvoiceTaxAmount >= 0
        if (request.InvoiceTaxAmount < 0)
        {
            errors[nameof(request.InvoiceTaxAmount)] = ["Invoice tax amount must be >= 0"];
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        return null;
    }

    private (string? ItemsXml, IActionResult? Error) ProcessAndValidateItems(TransactMRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ItemsXml))
        {
            return (null, CreateValidationError("Items XML is required"));
        }

        try
        {
            var doc = XDocument.Parse(request.ItemsXml);
            var items = doc.Descendants("item").ToList();

            if (items.Count == 0)
            {
                return (null, CreateValidationError("At least one item is required in ItemsXml"));
            }

            var errors = new Dictionary<string, string[]>();
            var errorList = new List<string>();

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var lineNumber = i + 1;

                // ITEMCODE required
                var itemCode = item.Element("ITEMCODE")?.Value;
                if (string.IsNullOrWhiteSpace(itemCode))
                {
                    errorList.Add($"Line {lineNumber}: ITEMCODE is required");
                }

                // QTY > 0
                var qtyStr = item.Element("QTY")?.Value;
                if (!decimal.TryParse(qtyStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
                {
                    errorList.Add($"Line {lineNumber}: QTY must be > 0");
                }

                // PRICE >= 0
                var priceStr = item.Element("PRICE")?.Value;
                if (!decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) || price < 0)
                {
                    errorList.Add($"Line {lineNumber}: PRICE must be >= 0");
                }

                // Calculate AMT = QTY × PRICE (CRITICAL RULE)
                var calculatedAmt = qty * price;

                // Update or set AMT element
                var amtElement = item.Element("AMT");
                if (amtElement != null)
                {
                    amtElement.Value = calculatedAmt.ToString("F2", CultureInfo.InvariantCulture);
                }
                else
                {
                    item.Add(new XElement("AMT", calculatedAmt.ToString("F2", CultureInfo.InvariantCulture)));
                }

                // Handle TAXR - derive from SAP rate or use configured VAT rate
                var taxrElement = item.Element("TAXR");
                var taxrStr = taxrElement?.Value;

                if (string.IsNullOrWhiteSpace(taxrStr) || !decimal.TryParse(taxrStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var taxr))
                {
                    // If no SAP VAT rate provided and VAT applies, use configured rate (15.5%)
                    // Check if this item is VAT exempt
                    var taxElement = item.Element("TAX")?.Value;
                    bool isVatExempt = string.Equals(taxElement, "0", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(taxElement, "exempt", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(taxElement, "E", StringComparison.OrdinalIgnoreCase);

                    if (isVatExempt)
                    {
                        taxr = 0m;
                    }
                    else
                    {
                        // Use configured VAT rate (15.5% = 0.155)
                        taxr = _settings.VatRate;
                    }

                    if (taxrElement != null)
                    {
                        taxrElement.Value = taxr.ToString("F4", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        item.Add(new XElement("TAXR", taxr.ToString("F4", CultureInfo.InvariantCulture)));
                    }
                }
                else if (taxr > 1)
                {
                    // SAP provides percentage (e.g., 15.5), convert to decimal (0.155)
                    taxr = taxr / 100m;
                    taxrElement!.Value = taxr.ToString("F4", CultureInfo.InvariantCulture);
                }

                // Ensure TAX element exists (fiscal tax ID)
                var tax = item.Element("TAX");
                if (tax == null)
                {
                    item.Add(new XElement("TAX", "0"));
                }
            }

            if (errorList.Count > 0)
            {
                errors["Items"] = errorList.ToArray();
                return (null, ValidationProblem(new ValidationProblemDetails(errors)));
            }

            return (doc.ToString(SaveOptions.DisableFormatting), null);
        }
        catch (System.Xml.XmlException ex)
        {
            _logger.LogWarning(ex, "Failed to parse ItemsXml");
            return (null, CreateValidationError($"Invalid ItemsXml format: {ex.Message}"));
        }
    }

    #endregion

    #region Credit Note Validation

    private async Task<(InvoiceResponse? Invoice, IActionResult? Error)> ValidateOriginalInvoiceAsync(
        string originalInvoiceNumber,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "Validating original invoice {OriginalInvoiceNumber} for credit note. CorrelationId: {CorrelationId}",
                originalInvoiceNumber, correlationId);

            var invoice = await _revmaxClient.GetInvoiceAsync(originalInvoiceNumber, cancellationToken);

            // Check if invoice exists and has fiscal evidence
            if (invoice == null || !invoice.Success)
            {
                return (null, BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Original Invoice Not Found",
                    Detail = $"Original invoice not found on Revmax: {originalInvoiceNumber}",
                    Instance = HttpContext.Request.Path,
                    Extensions = { ["correlationId"] = correlationId }
                }));
            }

            // Check for fiscal evidence (best effort):
            // - QRcode exists OR
            // - Data.receiptGlobalNo > 0
            bool hasFiscalEvidence = !string.IsNullOrWhiteSpace(invoice.QRcode) ||
                                     (invoice.Data?.ReceiptGlobalNo > 0);

            if (!hasFiscalEvidence)
            {
                return (null, BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Original Invoice Not Fiscalized",
                    Detail = $"Original invoice not found on Revmax: {originalInvoiceNumber}",
                    Instance = HttpContext.Request.Path,
                    Extensions = { ["correlationId"] = correlationId }
                }));
            }

            _logger.LogInformation(
                "Original invoice {OriginalInvoiceNumber} validated. ReceiptGlobalNo: {ReceiptGlobalNo}, FiscalDayNo: {FiscalDayNo}",
                originalInvoiceNumber, invoice.Data?.ReceiptGlobalNo, invoice.Data?.FiscalDayNo);

            return (invoice, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Failed to validate original invoice {OriginalInvoiceNumber}. CorrelationId: {CorrelationId}",
                originalInvoiceNumber, correlationId);

            return (null, BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Original Invoice Validation Failed",
                Detail = $"Original invoice not found on Revmax: {originalInvoiceNumber}",
                Instance = HttpContext.Request.Path,
                Extensions = { ["correlationId"] = correlationId }
            }));
        }
    }

    private async Task<(bool IsDuplicate, IActionResult? Error)> CheckDuplicateFiscalizationAsync(
        string invoiceNumber,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "Checking for duplicate fiscalization of {InvoiceNumber}. CorrelationId: {CorrelationId}",
                invoiceNumber, correlationId);

            var existingInvoice = await _revmaxClient.GetInvoiceAsync(invoiceNumber, cancellationToken);

            // Check if already fiscalized
            if (existingInvoice != null && existingInvoice.Success)
            {
                bool hasFiscalEvidence = !string.IsNullOrWhiteSpace(existingInvoice.QRcode) ||
                                         (existingInvoice.Data?.ReceiptGlobalNo > 0);

                if (hasFiscalEvidence)
                {
                    return (true, Conflict(new ProblemDetails
                    {
                        Status = StatusCodes.Status409Conflict,
                        Title = "Duplicate Fiscalization",
                        Detail = $"The credit note number is already fiscalized: {invoiceNumber}",
                        Instance = HttpContext.Request.Path,
                        Extensions = { ["correlationId"] = correlationId }
                    }));
                }
            }

            return (false, null);
        }
        catch (HttpRequestException)
        {
            // Invoice not found - this is expected, no duplicate
            return (false, null);
        }
    }

    #endregion

    #region Helper Methods

    private void ApplyDefaults(TransactMRequest request)
    {
        request.Currency ??= _settings.DefaultCurrency;
        request.BranchName ??= _settings.DefaultBranchName;
    }

    private string GetCorrelationId()
    {
        if (HttpContext.Request.Headers.TryGetValue("X-Correlation-ID", out var correlationId) &&
            !string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId!;
        }

        return Guid.NewGuid().ToString("N");
    }

    private IActionResult CreateValidationError(string message)
    {
        ModelState.AddModelError("", message);
        return ValidationProblem(ModelState);
    }

    private IActionResult CreateUpstreamErrorResponse(HttpRequestException ex, string operation, string correlationId)
    {
        _logger.LogError(ex, "Upstream error during {Operation}. CorrelationId: {CorrelationId}", operation, correlationId);

        var statusCode = ex.StatusCode.HasValue
            ? (int)ex.StatusCode.Value
            : StatusCodes.Status502BadGateway;

        var detail = ex.Message;
        if (detail.Length > 4096)
        {
            detail = detail[..4096] + "... [truncated]";
        }

        return StatusCode(statusCode, new ProblemDetails
        {
            Status = statusCode,
            Title = "Upstream Error",
            Detail = detail,
            Instance = HttpContext.Request.Path,
            Extensions =
            {
                ["correlationId"] = correlationId,
                ["operation"] = operation
            }
        });
    }

    #endregion
}
