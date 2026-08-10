using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Services;

/// <summary>
/// Fiscalises invoices and credit notes against the ZIMRA FDMS Fiscalisation platform.
/// </summary>
public interface IFiscalizationService
{
    /// <summary>
    /// Fiscalises an invoice that has already been posted to SAP.
    /// </summary>
    /// <remarks>
    /// Only the SAP DocEntry is sent — the platform reads the document's lines, buyer and totals from
    /// SAP itself. Do not call this for anything that is not in SAP: the DocEntry is looked up for
    /// real, so a local identifier passed here fiscalises whichever unrelated SAP invoice holds that
    /// number. Use <see cref="FiscalizePreSapInvoiceAsync"/> instead.
    /// </remarks>
    Task<FiscalizationResult> FiscalizeInvoiceAsync(
        InvoiceDto invoice,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fiscalises an invoice that does not exist in SAP yet, from a full receipt payload.
    /// </summary>
    /// <param name="externalReference">
    /// Stable identifier for the document. It becomes the receipt's permanent fiscal identity and part
    /// of the platform's idempotency key, so it must be identical on every retry and must never be
    /// regenerated.
    /// </param>
    Task<FiscalizationResult> FiscalizePreSapInvoiceAsync(
        InvoiceDto invoice,
        string externalReference,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fiscalises a credit note that has already been posted to SAP.
    /// </summary>
    /// <param name="originalInvoiceNumber">
    /// The invoice being credited. Advisory only — recorded for traceability. The platform recovers the
    /// fiscal link itself from the SAP document's BaseType/BaseEntry, across all devices, which is
    /// strictly better than anything this app can compute.
    /// </param>
    Task<FiscalizationResult> FiscalizeCreditNoteAsync(
        InvoiceDto creditNote,
        string originalInvoiceNumber,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a document has already been fiscalised, on any device.
    /// </summary>
    Task<bool> IsInvoiceFiscalizedAsync(string invoiceNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Customer fiscal details.
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
    /// Whether fiscalization was skipped (not configured, already done, or a server-side dry run).
    /// </summary>
    public bool Skipped { get; set; }

    /// <summary>
    /// Whether fiscalization was accepted for background processing.
    /// </summary>
    public bool Queued { get; set; }

    /// <summary>
    /// The document's fiscal state is unknown and must be reconciled by looking it up, not by
    /// resubmitting.
    /// </summary>
    /// <remarks>
    /// Set on an idempotency conflict or an indeterminate FDMS outcome. Callers with retry logic must
    /// check this before retrying: a receipt may already exist at FDMS, and a duplicate fiscal receipt
    /// cannot be withdrawn.
    /// </remarks>
    public bool RequiresReconciliation { get; set; }

    public string? RawRequestJson { get; set; }

    public string? RawResponseJson { get; set; }
}

/// <summary>
/// Implementation backed by the Fiscalisation platform's HTTP API.
/// </summary>
public class FiscalizationService : IFiscalizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly IFiscalisationApiClient _client;
    private readonly IFiscalDeviceConfigCache _configCache;
    private readonly FiscalisationSettings _settings;
    private readonly ILogger<FiscalizationService> _logger;

    public FiscalizationService(
        IFiscalisationApiClient client,
        IFiscalDeviceConfigCache configCache,
        IOptions<FiscalisationSettings> settings,
        ILogger<FiscalizationService> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _configCache = configCache ?? throw new ArgumentNullException(nameof(configCache));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<FiscalizationResult> FiscalizeInvoiceAsync(
        InvoiceDto invoice,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        return FiscaliseSapDocumentAsync(
            invoice,
            SapDocumentType.Invoice,
            ReceiptType.FiscalInvoice,
            customerDetails,
            cancellationToken);
    }

    public Task<FiscalizationResult> FiscalizeCreditNoteAsync(
        InvoiceDto creditNote,
        string originalInvoiceNumber,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creditNote);

        _logger.LogInformation(
            "Fiscalising credit note DocEntry {DocEntry} (credits {OriginalInvoice})",
            creditNote.DocEntry,
            originalInvoiceNumber);

        return FiscaliseSapDocumentAsync(
            creditNote,
            SapDocumentType.CreditNote,
            ReceiptType.CreditNote,
            customerDetails,
            cancellationToken);
    }

    /// <summary>
    /// Shared path for anything that already exists in SAP.
    /// </summary>
    private async Task<FiscalizationResult> FiscaliseSapDocumentAsync(
        InvoiceDto document,
        SapDocumentType documentType,
        ReceiptType receiptType,
        CustomerFiscalDetails? customerDetails,
        CancellationToken cancellationToken)
    {
        var documentNumber = document.DocNum.ToString(CultureInfo.InvariantCulture);

        if (!_settings.Enabled)
        {
            return Disabled(documentNumber);
        }

        if (document.DocEntry <= 0)
        {
            return new FiscalizationResult
            {
                Success = false,
                Message = "This document has no SAP DocEntry, so it cannot be fiscalised from SAP.",
                InvoiceNumber = documentNumber,
                ErrorCode = "MISSING_DOC_ENTRY"
            };
        }

        // No lines, no currency, no invoice number. The platform reads all of that from SAP, and
        // letting it derive the invoice number from the document guarantees the idempotency key
        // matches whatever a later re-run or reconciliation computes.
        var request = new SapFiscaliseReceiptApiRequest
        {
            SapDocument = new SapDocumentReference
            {
                DocumentType = documentType,
                DocEntry = document.DocEntry
            },
            Receipt = new SubmitReceiptApiRequest
            {
                // Device 0 lets the platform fiscalise on whichever of its devices is healthy.
                DeviceId = 0,
                ReceiptType = receiptType,
                Buyer = MapBuyer(customerDetails),
                ReceiptNotes = document.Comments
            }
        };

        var rawRequestJson = Serialize(request);

        try
        {
            var response = await _client.SubmitSapReceiptAsync(request, cancellationToken);

            _logger.LogInformation(
                "Fiscalised {DocumentType} {InvoiceNo}. ReceiptGlobalNo: {ReceiptGlobalNo}, FiscalDayNo: {FiscalDayNo}",
                documentType,
                response.InvoiceNo,
                response.ReceiptGlobalNo,
                response.FiscalDayNo);

            return await MapSuccessAsync(response, rawRequestJson, cancellationToken);
        }
        catch (FiscalisationApiException ex)
        {
            return await MapFailureAsync(ex, documentNumber, receiptType, rawRequestJson, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unexpected(ex, documentNumber, rawRequestJson);
        }
    }

    public async Task<FiscalizationResult> FiscalizePreSapInvoiceAsync(
        InvoiceDto invoice,
        string externalReference,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (string.IsNullOrWhiteSpace(externalReference))
        {
            throw new ArgumentException(
                "A stable external reference is required: it becomes the receipt's permanent fiscal identity.",
                nameof(externalReference));
        }

        var invoiceNo = BuildPreSapInvoiceNo(externalReference);

        if (!_settings.Enabled)
        {
            return Disabled(invoiceNo);
        }

        var lines = MapLines(invoice, ReceiptType.FiscalInvoice);
        if (lines.Count == 0)
        {
            return new FiscalizationResult
            {
                Success = false,
                Message = "The invoice has no lines to fiscalise.",
                InvoiceNumber = invoiceNo,
                ErrorCode = "NO_LINES"
            };
        }

        var request = new SubmitReceiptApiRequest
        {
            DeviceId = _settings.DefaultDeviceId,
            InvoiceNo = invoiceNo,
            ReceiptType = ReceiptType.FiscalInvoice,
            Currency = string.IsNullOrWhiteSpace(invoice.DocCurrency)
                ? _settings.DefaultCurrency
                : invoice.DocCurrency,
            ReceiptDate = ParseDocDate(invoice.DocDate),
            TaxInclusive = true,
            PaymentType = MoneyType.Cash,
            PaymentAmount = lines.Sum(line => RoundCurrency(line.Price * line.Quantity)),
            Lines = lines,
            Buyer = MapBuyer(customerDetails),
            ReceiptNotes = invoice.Comments,
            ReceiptPrintForm = ReceiptPrintForm.InvoiceA4
        };

        var rawRequestJson = Serialize(request);

        try
        {
            var response = await _client.SubmitReceiptAsync(request, cancellationToken);

            _logger.LogInformation(
                "Fiscalised pre-SAP invoice {InvoiceNo}. ReceiptGlobalNo: {ReceiptGlobalNo}",
                invoiceNo,
                response.ReceiptGlobalNo);

            return await MapSuccessAsync(response, rawRequestJson, cancellationToken);
        }
        catch (FiscalisationApiException ex)
        {
            return await MapFailureAsync(
                ex, invoiceNo, ReceiptType.FiscalInvoice, rawRequestJson, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unexpected(ex, invoiceNo, rawRequestJson);
        }
    }

    public async Task<bool> IsInvoiceFiscalizedAsync(
        string invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber) || !_settings.Enabled)
        {
            return false;
        }

        try
        {
            // Device 0 searches every device: an earlier attempt may have failed over.
            var result = await _client.CheckReceiptAsync(
                0, invoiceNumber, ReceiptType.FiscalInvoice, cancellationToken);

            return result.IsFiscalised;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error checking fiscalisation status for {InvoiceNumber}", invoiceNumber);
            return false;
        }
    }

    /// <summary>
    /// Lifts a purely numeric reference out of the SAP DocNum namespace. See
    /// <see cref="FiscalisationSettings.PreSapInvoiceNoPrefix"/> for why.
    /// </summary>
    /// <remarks>
    /// References that already start with a letter (DESKTOP-…, DS-…, SO-CONV-…) pass through unchanged,
    /// so anything already fiscalised keeps its existing fiscal identity.
    /// </remarks>
    internal string BuildPreSapInvoiceNo(string externalReference)
    {
        var trimmed = externalReference.Trim();

        return trimmed.All(char.IsAsciiDigit)
            ? _settings.PreSapInvoiceNoPrefix + trimmed
            : trimmed;
    }

    private async Task<FiscalizationResult> MapSuccessAsync(
        SubmitReceiptApiResponse response,
        string? rawRequestJson,
        CancellationToken cancellationToken)
    {
        var config = await _configCache.TryGetAsync(response.DeviceId, cancellationToken);
        var verificationCode = FiscalReceiptQrComposer.TryCreateVerificationCode(response.DeviceSignatureValue);
        var qrPayload = FiscalReceiptQrComposer.BuildQrPayload(
            config?.QrUrl,
            response.DeviceId,
            response.ReceiptDate,
            response.ReceiptGlobalNo,
            verificationCode);

        if (qrPayload is null)
        {
            // The receipt is already at FDMS and cannot be withdrawn, so a missing QR is never a reason
            // to fail or retry. The status backfill repairs it later.
            _logger.LogWarning(
                "Fiscalised {InvoiceNo} but could not compose its QR code: {Reason}",
                response.InvoiceNo,
                FiscalReceiptQrComposer.ResolveUnavailableReason(config?.QrUrl, verificationCode));
        }

        return new FiscalizationResult
        {
            Success = response.Success,
            Message = $"Fiscalised as receipt {response.ReceiptGlobalNo} on day {response.FiscalDayNo}.",
            InvoiceNumber = response.InvoiceNo,
            QRCode = qrPayload,
            VerificationCode = verificationCode is null
                ? null
                : FiscalReceiptQrComposer.FormatVerificationCode(verificationCode),
            FiscalDayNo = response.FiscalDayNo.ToString(CultureInfo.InvariantCulture),
            ReceiptGlobalNo = response.ReceiptGlobalNo.ToString(CultureInfo.InvariantCulture),
            ReceiptCounter = response.ReceiptCounter.ToString(CultureInfo.InvariantCulture),
            DeviceSerial = config?.DeviceSerialNo,
            RawRequestJson = rawRequestJson,
            RawResponseJson = Serialize(response)
        };
    }

    /// <summary>
    /// Turns a platform error into a result, deciding whether it is a success in disguise, a
    /// reconcile-don't-retry conflict, or a real failure.
    /// </summary>
    private async Task<FiscalizationResult> MapFailureAsync(
        FiscalisationApiException exception,
        string invoiceNumber,
        ReceiptType receiptType,
        string? rawRequestJson,
        CancellationToken cancellationToken)
    {
        // "Already fiscalised" arrives as a 400 from the SAP endpoint, not as a replayed success. Left
        // as a failure it would raise a fresh incident every time a completed invoice is reprocessed.
        if (string.Equals(exception.ErrorCode, "AlreadyFiscalised", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "{InvoiceNumber} is already fiscalised; recovering its receipt details.",
                invoiceNumber);

            return await RecoverAlreadyFiscalisedAsync(
                invoiceNumber, receiptType, rawRequestJson, exception, cancellationToken);
        }

        // The platform is in dry-run mode: it mapped and logged the receipt but sent nothing to FDMS.
        if (string.Equals(exception.ErrorCode, "DryRun", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Fiscalisation platform is in dry-run mode; {InvoiceNumber} was not sent to FDMS.",
                invoiceNumber);

            return new FiscalizationResult
            {
                Success = false,
                Skipped = true,
                Message = "The fiscalisation platform is in dry-run mode; nothing was submitted to FDMS.",
                InvoiceNumber = invoiceNumber,
                ErrorCode = exception.ErrorCode,
                RawRequestJson = rawRequestJson
            };
        }

        if (exception.RequiresReconciliation)
        {
            _logger.LogError(
                exception,
                "Fiscalisation of {InvoiceNumber} is unresolved ({ErrorCode}). It must be reconciled, not retried.",
                invoiceNumber,
                exception.ErrorCode);

            return new FiscalizationResult
            {
                Success = false,
                RequiresReconciliation = true,
                Message = "The fiscal outcome is unresolved. Check the receipt on the fiscalisation "
                    + "console before any resubmission — it may already exist.",
                InvoiceNumber = invoiceNumber,
                ErrorCode = exception.ErrorCode,
                ErrorDetails = exception.Message,
                RawRequestJson = rawRequestJson
            };
        }

        _logger.LogError(
            exception,
            "Fiscalisation failed for {InvoiceNumber} ({ErrorCode})",
            invoiceNumber,
            exception.ErrorCode);

        return new FiscalizationResult
        {
            Success = false,
            Message = exception.Message,
            InvoiceNumber = invoiceNumber,
            ErrorCode = exception.ErrorCode ?? "FISCALISATION_ERROR",
            ErrorDetails = exception.Message,
            RawRequestJson = rawRequestJson
        };
    }

    private async Task<FiscalizationResult> RecoverAlreadyFiscalisedAsync(
        string invoiceNumber,
        ReceiptType receiptType,
        string? rawRequestJson,
        FiscalisationApiException exception,
        CancellationToken cancellationToken)
    {
        try
        {
            var check = await _client.CheckReceiptAsync(0, invoiceNumber, receiptType, cancellationToken);
            var match = check.Matches.FirstOrDefault();

            if (match is not null)
            {
                var config = await _configCache.TryGetAsync(match.DeviceId, cancellationToken);
                var verificationCode = FiscalReceiptQrComposer.TryCreateVerificationCode(match.DeviceSignatureValue);

                return new FiscalizationResult
                {
                    Success = true,
                    Skipped = true,
                    Message = $"Already fiscalised as receipt {match.ReceiptGlobalNo} on device {match.DeviceId}.",
                    InvoiceNumber = match.InvoiceNo,
                    QRCode = FiscalReceiptQrComposer.BuildQrPayload(
                        config?.QrUrl, match.DeviceId, match.ReceiptDate, match.ReceiptGlobalNo, verificationCode),
                    VerificationCode = verificationCode is null
                        ? null
                        : FiscalReceiptQrComposer.FormatVerificationCode(verificationCode),
                    FiscalDayNo = match.FiscalDayNo.ToString(CultureInfo.InvariantCulture),
                    ReceiptGlobalNo = match.ReceiptGlobalNo.ToString(CultureInfo.InvariantCulture),
                    ReceiptCounter = match.ReceiptCounter.ToString(CultureInfo.InvariantCulture),
                    DeviceSerial = config?.DeviceSerialNo,
                    RawRequestJson = rawRequestJson,
                    RawResponseJson = Serialize(check)
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "Could not read back the existing receipt for {InvoiceNumber}", invoiceNumber);
        }

        // Still a success: the platform told us the document is fiscalised. We just could not recover
        // the receipt details to display.
        return new FiscalizationResult
        {
            Success = true,
            Skipped = true,
            Message = exception.Message,
            InvoiceNumber = invoiceNumber,
            ErrorCode = exception.ErrorCode,
            RawRequestJson = rawRequestJson
        };
    }

    private FiscalizationResult Disabled(string invoiceNumber)
    {
        _logger.LogInformation("Fiscalisation is disabled; skipping {InvoiceNumber}", invoiceNumber);

        return new FiscalizationResult
        {
            Success = true,
            Skipped = true,
            Message = "Fiscalisation is disabled.",
            InvoiceNumber = invoiceNumber
        };
    }

    private FiscalizationResult Unexpected(Exception ex, string invoiceNumber, string? rawRequestJson)
    {
        _logger.LogError(ex, "Unexpected error fiscalising {InvoiceNumber}", invoiceNumber);

        return new FiscalizationResult
        {
            Success = false,
            Message = "Fiscalisation error",
            InvoiceNumber = invoiceNumber,
            ErrorCode = "UNEXPECTED_ERROR",
            ErrorDetails = ex.Message,
            RawRequestJson = rawRequestJson
        };
    }

    private static BuyerApiRequest? MapBuyer(CustomerFiscalDetails? customerDetails)
    {
        if (customerDetails is null)
        {
            return null;
        }

        return new BuyerApiRequest
        {
            RegisterName = customerDetails.CustomerName,
            TradeName = customerDetails.CustomerName,
            Tin = customerDetails.BPN,
            VatNumber = customerDetails.VatNumber,
            Phone = customerDetails.Telephone,
            Email = customerDetails.Email,
            Street = customerDetails.Address
        };
    }

    /// <summary>
    /// Maps invoice lines to receipt lines, satisfying the platform's line validation.
    /// </summary>
    /// <remarks>
    /// Prices are tax-inclusive gross, and negative for a credit note — the platform rejects a credit
    /// note whose line prices are positive.
    /// </remarks>
    private List<LineApiRequest> MapLines(InvoiceDto invoice, ReceiptType receiptType)
    {
        if (invoice.Lines is null || invoice.Lines.Count == 0)
        {
            return [];
        }

        var sign = receiptType == ReceiptType.CreditNote ? -1m : 1m;

        return invoice.Lines
            .Select(line =>
            {
                var quantity = Math.Abs(line.Quantity);
                var price = RoundCurrency(GetPriceAfterVat(line));

                return new LineApiRequest
                {
                    // Required, and capped at 200 characters by the platform. ItemDescription is
                    // nullable, so fall back to the code rather than sending an empty name.
                    Name = Truncate(
                        string.IsNullOrWhiteSpace(line.ItemDescription) ? line.ItemCode : line.ItemDescription,
                        200),
                    HsCode = _settings.DefaultHsCode,
                    Quantity = quantity <= 0m ? 1m : quantity,
                    Price = sign * price,
                    TaxId = ResolveTaxId(line.TaxCode)
                };
            })
            .ToList();
    }

    private int ResolveTaxId(string? taxCode)
    {
        if (!string.IsNullOrWhiteSpace(taxCode)
            && _settings.TaxIdMappings.TryGetValue(taxCode.Trim(), out var taxId)
            && taxId > 0)
        {
            return taxId;
        }

        return _settings.DefaultTaxId;
    }

    private static string? Truncate(string? value, int maxLength)
        => value is not null && value.Length > maxLength ? value[..maxLength] : value;

    private static decimal GetPriceAfterVat(InvoiceLineDto line)
    {
        var grossPrice = Math.Abs(line.GrossPrice);
        return grossPrice > 0m ? grossPrice : Math.Abs(line.UnitPrice);
    }

    private static DateTime? ParseDocDate(string? docDate)
        => DateTime.TryParse(docDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static decimal RoundCurrency(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
}
