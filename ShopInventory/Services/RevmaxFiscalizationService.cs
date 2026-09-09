using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models.Revmax;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Services;

/// <summary>
/// Fiscalises against the REVMax device at <c>Revmax:BaseUrl</c>.
/// </summary>
/// <remarks>
/// This is the ZIMRA-approved path and the one that runs. <see cref="FiscalizationService"/> talks to
/// the in-house Fiscalisation platform, which is still awaiting ZIMRA approval and is dormant — see
/// <c>Fiscalisation:Provider</c>.
///
/// REVMax takes a complete receipt: it reads nothing from SAP, so the post-SAP and pre-SAP paths build
/// the same payload. The only difference between them is the invoice number the receipt is filed under.
/// </remarks>
public class RevmaxFiscalizationService : IFiscalizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>Istatus for an ordinary invoice.</summary>
    private const string InvoiceStatus = "01";

    /// <summary>Istatus for a credit note.</summary>
    private const string CreditNoteStatus = "02";

    private readonly IRevmaxClient _client;
    private readonly RevmaxSettings _settings;
    private readonly TaxSettings _taxSettings;
    private readonly FiscalisationSettings _fiscalisationSettings;
    private readonly ILogger<RevmaxFiscalizationService> _logger;

    public RevmaxFiscalizationService(
        IRevmaxClient client,
        IOptions<RevmaxSettings> settings,
        IOptions<TaxSettings> taxSettings,
        IOptions<FiscalisationSettings> fiscalisationSettings,
        ILogger<RevmaxFiscalizationService> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _taxSettings = taxSettings?.Value ?? throw new ArgumentNullException(nameof(taxSettings));
        _fiscalisationSettings = fiscalisationSettings?.Value
            ?? throw new ArgumentNullException(nameof(fiscalisationSettings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<FiscalizationResult> FiscalizeInvoiceAsync(
        InvoiceDto invoice,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        return FiscaliseInvoiceAsync(
            invoice,
            invoice.DocNum.ToString(CultureInfo.InvariantCulture),
            customerDetails,
            cancellationToken);
    }

    /// <remarks>
    /// <paramref name="paymentType"/> is accepted and not sent. REVMax has no tender field: its
    /// receipt declares money received as CurrenciesXml — currency, amount and rate — and derives the
    /// payment breakdown itself. The parameter stays on the interface because the platform path needs
    /// it, and dropping it here would silently change that path's behaviour if the provider is
    /// switched back.
    /// </remarks>
    public Task<FiscalizationResult> FiscalizePreSapInvoiceAsync(
        InvoiceDto invoice,
        string externalReference,
        CustomerFiscalDetails? customerDetails = null,
        MoneyType? paymentType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (string.IsNullOrWhiteSpace(externalReference))
        {
            throw new ArgumentException(
                "A stable external reference is required: it becomes the receipt's permanent fiscal identity.",
                nameof(externalReference));
        }

        // REVMax keys a receipt on its invoice number alone, so a bare numeric pre-SAP reference shares
        // a namespace with every present and future SAP DocNum. Prefixing lifts it out, exactly as on
        // the platform path.
        return FiscaliseInvoiceAsync(
            invoice,
            BuildPreSapInvoiceNo(externalReference),
            customerDetails,
            cancellationToken);
    }

    private async Task<FiscalizationResult> FiscaliseInvoiceAsync(
        InvoiceDto invoice,
        string invoiceNumber,
        CustomerFiscalDetails? customerDetails,
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return Disabled(invoiceNumber);
        }

        var request = BuildInvoiceRequest(invoice, invoiceNumber, customerDetails);
        var rawRequestJson = Serialize(request);

        try
        {
            var response = await _client.TransactMAsync(request, cancellationToken);
            return MapResponse(response, invoiceNumber, rawRequestJson);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return await ReconcileIndeterminateAsync(ex, invoiceNumber, rawRequestJson, cancellationToken);
        }
    }

    public async Task<FiscalizationResult> FiscalizeCreditNoteAsync(
        InvoiceDto creditNote,
        string originalInvoiceNumber,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creditNote);

        var invoiceNumber = creditNote.DocNum.ToString(CultureInfo.InvariantCulture);

        if (!_settings.Enabled)
        {
            return Disabled(invoiceNumber);
        }

        // The credit note must point at the receipt it reverses. Unlike the platform, REVMax recovers
        // nothing on its own: refDeviceId/refReceiptGlobalNo/refFiscalDayNo are what tie the two
        // together at ZIMRA, and they come from the original receipt.
        InvoiceResponse? original = null;

        if (!string.IsNullOrWhiteSpace(originalInvoiceNumber))
        {
            try
            {
                original = await _client.GetInvoiceAsync(originalInvoiceNumber, cancellationToken);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Could not read the original invoice {OriginalInvoiceNumber} from REVMax while "
                    + "fiscalising credit note {InvoiceNumber}.",
                    originalInvoiceNumber,
                    invoiceNumber);
            }
        }

        if (original?.Success != true)
        {
            // Refuse rather than file an unlinked credit note. ZIMRA cannot associate a credit note
            // whose reference fields are absent or zero, and the receipt cannot be withdrawn to fix it.
            return new FiscalizationResult
            {
                Success = false,
                InvoiceNumber = invoiceNumber,
                ErrorCode = "ORIGINAL_RECEIPT_NOT_FOUND",
                Message =
                    $"Not fiscalising credit note {invoiceNumber}: REVMax holds no receipt for the "
                    + $"original invoice {originalInvoiceNumber}, so the credit note has nothing to "
                    + "reference. " + (original?.Message ?? "The original invoice could not be read.")
            };
        }

        var request = BuildCreditNoteRequest(
            creditNote, invoiceNumber, originalInvoiceNumber, original, customerDetails);
        var rawRequestJson = Serialize(request);

        try
        {
            var response = await _client.TransactMExtAsync(request, cancellationToken);
            return MapResponse(response, invoiceNumber, rawRequestJson);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return await ReconcileIndeterminateAsync(ex, invoiceNumber, rawRequestJson, cancellationToken);
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
            var response = await _client.GetInvoiceAsync(invoiceNumber, cancellationToken);
            return response?.Success == true;
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Answers false when it cannot ask. That is this method's documented contract and the
            // reason FindPreSapReceiptAsync exists separately: this one must never be the guard that
            // decides whether it is safe to sign.
            _logger.LogWarning(
                ex, "Could not ask REVMax whether {InvoiceNumber} is fiscalised.", invoiceNumber);
            return false;
        }
    }

    public async Task<FiscalizationResult?> FindPreSapReceiptAsync(
        string externalReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalReference) || !_settings.Enabled)
        {
            return null;
        }

        var invoiceNumber = BuildPreSapInvoiceNo(externalReference);

        InvoiceResponse? response;
        try
        {
            response = await _client.GetInvoiceAsync(invoiceNumber, cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Throws rather than returning null: treating "I could not check" as "there is nothing
            // there" is exactly how a second fiscal receipt gets signed.
            throw new InvalidOperationException(
                $"Not fiscalising {invoiceNumber}: REVMax could not be asked whether it already holds "
                + $"this receipt. {ex.Message}", ex);
        }

        if (response is null)
        {
            throw new InvalidOperationException(
                $"Not fiscalising {invoiceNumber}: REVMax returned no answer when asked whether it "
                + "already holds this receipt.");
        }

        if (!response.Success)
        {
            // A positive "Invoice not Found" — safe to sign.
            return null;
        }

        _logger.LogWarning(
            "Adopting the fiscal receipt REVMax already holds for {InvoiceNumber} rather than signing "
            + "it again (receipt {ReceiptGlobalNo}).",
            invoiceNumber,
            response.Data?.ReceiptGlobalNo);

        return new FiscalizationResult
        {
            Success = true,
            Message =
                $"Receipt {response.Data?.ReceiptGlobalNo} was already signed for this sale; adopted it.",
            InvoiceNumber = invoiceNumber,
            ReceiptGlobalNo = response.Data?.ReceiptGlobalNo.ToString(CultureInfo.InvariantCulture),
            ReceiptCounter = response.Data?.ReceiptCounter.ToString(CultureInfo.InvariantCulture),
            FiscalDayNo = NullIfBlank(response.FiscalDay),
            DeviceSerial = NullIfBlank(response.DeviceSerialNumber) ?? NullIfBlank(response.Data?.DeviceSerial),
            QRCode = NullIfBlank(response.QRcode),
            VerificationCode = NullIfBlank(response.VerificationCode)
        };
    }

    /// <summary>
    /// Resolves an attempt whose outcome never came back, by asking REVMax what it holds.
    /// </summary>
    /// <remarks>
    /// A timeout or a 5xx does not say whether the receipt was filed, and resubmitting is how one
    /// invoice acquires two fiscal receipts. The POST is never retried; this asks instead. Only a
    /// definite "not found" is reported as a plain failure the caller may retry — anything else sets
    /// <see cref="FiscalizationResult.RequiresReconciliation"/>.
    /// </remarks>
    private async Task<FiscalizationResult> ReconcileIndeterminateAsync(
        Exception ex,
        string invoiceNumber,
        string? rawRequestJson,
        CancellationToken cancellationToken)
    {
        _logger.LogError(
            ex,
            "REVMax did not answer for {InvoiceNumber}. Asking it what it holds before deciding "
            + "anything — the receipt may already be filed.",
            invoiceNumber);

        try
        {
            var existing = await _client.GetInvoiceAsync(invoiceNumber, cancellationToken);

            if (existing?.Success == true)
            {
                _logger.LogWarning(
                    "REVMax had already filed {InvoiceNumber} (receipt {ReceiptGlobalNo}) despite the "
                    + "failed call. Adopting it.",
                    invoiceNumber,
                    existing.Data?.ReceiptGlobalNo);

                return new FiscalizationResult
                {
                    Success = true,
                    Message =
                        $"The call failed but REVMax had filed receipt {existing.Data?.ReceiptGlobalNo}; "
                        + "adopted it rather than signing again.",
                    InvoiceNumber = invoiceNumber,
                    ReceiptGlobalNo =
                        existing.Data?.ReceiptGlobalNo.ToString(CultureInfo.InvariantCulture),
                    ReceiptCounter =
                        existing.Data?.ReceiptCounter.ToString(CultureInfo.InvariantCulture),
                    FiscalDayNo = NullIfBlank(existing.FiscalDay),
                    DeviceSerial = NullIfBlank(existing.DeviceSerialNumber)
                        ?? NullIfBlank(existing.Data?.DeviceSerial),
                    QRCode = NullIfBlank(existing.QRcode),
                    VerificationCode = NullIfBlank(existing.VerificationCode),
                    RawRequestJson = rawRequestJson
                };
            }

            if (existing is not null)
            {
                // REVMax answered, and answered that it holds nothing. Safe to retry.
                return new FiscalizationResult
                {
                    Success = false,
                    Message =
                        $"REVMax did not accept {invoiceNumber} and holds no receipt for it. {ex.Message}",
                    InvoiceNumber = invoiceNumber,
                    ErrorCode = "REVMAX_UNAVAILABLE",
                    ErrorDetails = ex.ToString(),
                    RawRequestJson = rawRequestJson
                };
            }
        }
        catch (Exception lookupEx) when (
            lookupEx is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                lookupEx,
                "Could not reconcile {InvoiceNumber} against REVMax after a failed submission.",
                invoiceNumber);
        }

        return new FiscalizationResult
        {
            Success = false,
            RequiresReconciliation = true,
            Message =
                $"The fiscal state of {invoiceNumber} is unknown: REVMax neither confirmed the "
                + "submission nor could be asked what it holds. Look it up before resubmitting — a "
                + "duplicate fiscal receipt cannot be withdrawn.",
            InvoiceNumber = invoiceNumber,
            ErrorCode = "REVMAX_INDETERMINATE",
            ErrorDetails = ex.ToString(),
            RawRequestJson = rawRequestJson
        };
    }

    private FiscalizationResult MapResponse(
        TransactMResponse? response,
        string invoiceNumber,
        string? rawRequestJson)
    {
        if (response is null)
        {
            return new FiscalizationResult
            {
                Success = false,
                RequiresReconciliation = true,
                Message =
                    $"REVMax returned an empty body for {invoiceNumber}. Look the invoice up before "
                    + "resubmitting.",
                InvoiceNumber = invoiceNumber,
                ErrorCode = "REVMAX_EMPTY_RESPONSE",
                RawRequestJson = rawRequestJson
            };
        }

        var rawResponseJson = Serialize(response);

        if (!response.Success)
        {
            return new FiscalizationResult
            {
                Success = false,
                Message = response.Message ?? $"REVMax refused {invoiceNumber}.",
                InvoiceNumber = invoiceNumber,
                ErrorCode = string.IsNullOrWhiteSpace(response.Code)
                    ? "REVMAX_ERROR"
                    : $"REVMAX_{response.Code}",
                RawRequestJson = rawRequestJson,
                RawResponseJson = rawResponseJson
            };
        }

        return new FiscalizationResult
        {
            Success = true,
            Message = response.Message,
            InvoiceNumber = invoiceNumber,
            QRCode = NullIfBlank(response.QRcode),
            VerificationCode = NullIfBlank(response.VerificationCode),
            // FiscalDay is the device's current day and rides on every response; FiscalDayNo appears
            // only on some builds. Prefer the specific one where it is present.
            FiscalDayNo = NullIfBlank(response.FiscalDayNo) ?? NullIfBlank(response.FiscalDay),
            ReceiptGlobalNo = NullIfBlank(response.ReceiptGlobalNo),
            ReceiptCounter = NullIfBlank(response.ReceiptCounter),
            DeviceSerial = NullIfBlank(response.DeviceSerialNumber) ?? NullIfBlank(response.DeviceSerial),
            RawRequestJson = rawRequestJson,
            RawResponseJson = rawResponseJson
        };
    }

    private TransactMRequest BuildInvoiceRequest(
        InvoiceDto invoice,
        string invoiceNumber,
        CustomerFiscalDetails? customerDetails)
    {
        var request = new TransactMRequest();
        PopulateCommon(request, invoice, invoiceNumber, customerDetails);
        request.Istatus = InvoiceStatus;
        request.InvoiceComment = invoice.Comments;
        return request;
    }

    private TransactMExtRequest BuildCreditNoteRequest(
        InvoiceDto creditNote,
        string invoiceNumber,
        string originalInvoiceNumber,
        InvoiceResponse original,
        CustomerFiscalDetails? customerDetails)
    {
        var request = new TransactMExtRequest();
        PopulateCommon(request, creditNote, invoiceNumber, customerDetails);

        request.Istatus = CreditNoteStatus;
        request.OriginalInvoiceNumber = originalInvoiceNumber;
        request.InvoiceComment = creditNote.Comments ?? creditNote.Remarks;

        request.refDeviceId = ParseInt(original.DeviceID) ?? _settings.DefaultRefDeviceId;
        request.refReceiptGlobalNo = original.Data?.ReceiptGlobalNo;

        // The original receipt's fiscal day, off the response envelope. NOT InvoiceData.FiscalDayNo,
        // which is a compatibility shim hard-coded to 0 — the pre-2026-08 code read it and therefore
        // referenced fiscal day 0 on every credit note it filed.
        request.refFiscalDayNo = ParseInt(original.FiscalDay);

        return request;
    }

    private void PopulateCommon(
        TransactMRequest request,
        InvoiceDto document,
        string invoiceNumber,
        CustomerFiscalDetails? customerDetails)
    {
        request.InvoiceNumber = invoiceNumber;
        request.Currency = document.DocCurrency ?? _settings.DefaultCurrency;
        request.BranchName = _settings.DefaultBranchName;
        request.CustomerName = customerDetails?.CustomerName ?? document.CardName;
        request.CustomerVatNumber = customerDetails?.VatNumber ?? document.CustomerVatNo;
        request.CustomerAddress = customerDetails?.Address ?? document.BillToAddress;
        request.CustomerTelephone = customerDetails?.Telephone ?? document.CustomerPhone;
        request.CustomerEmail = customerDetails?.Email ?? document.CustomerEmail;
        request.CustomerBPN = customerDetails?.BPN ?? document.CustomerTinNumber;

        // Credit notes come off SAP negative; REVMax is told the magnitude and Istatus "02" carries the
        // sign.
        request.InvoiceAmount = RoundCurrency(Math.Abs(document.DocTotal));
        request.InvoiceTaxAmount = RoundCurrency(Math.Abs(document.VatSum));
        request.Cashier = document.CardCode;
        request.ItemsXml = BuildItems(document);
        request.CurrenciesXml = BuildCurrencies(document);
    }

    private List<RevmaxRequestItem> BuildItems(InvoiceDto document)
    {
        if (document.Lines is null || document.Lines.Count == 0)
        {
            return new List<RevmaxRequestItem>();
        }

        return document.Lines.Select(line =>
        {
            var quantity = Math.Abs(line.Quantity);
            var price = GetPriceAfterVat(line);
            var amount = GetLineAmount(line, quantity, price);
            var description = line.ItemDescription ?? string.Empty;
            var taxCode = NormalizeTaxCode(line.TaxCode);

            return new RevmaxRequestItem
            {
                HH = line.LineNum.ToString(CultureInfo.InvariantCulture),
                ItemCode = line.ItemCode ?? string.Empty,
                ItemName1 = description,
                ItemName2 = description,
                Qty = quantity.ToString(CultureInfo.InvariantCulture),
                Price = RoundCurrency(price).ToString(CultureInfo.InvariantCulture),
                Amt = RoundCurrency(amount).ToString(CultureInfo.InvariantCulture),
                Tax = ResolveTaxId(taxCode).ToString(CultureInfo.InvariantCulture),
                TaxR = FormatTaxRate(ResolveTaxRate(taxCode))
            };
        }).ToList();
    }

    private List<RevmaxRequestCurrency> BuildCurrencies(InvoiceDto document)
        => new()
        {
            new RevmaxRequestCurrency
            {
                Name = document.DocCurrency ?? _settings.DefaultCurrency,
                Amount = RoundCurrency(Math.Abs(document.DocTotal))
                    .ToString(CultureInfo.InvariantCulture),
                Rate = "1"
            }
        };

    /// <summary>REVMax tax id for a SAP VAT group code.</summary>
    private int ResolveTaxId(string? taxCode)
    {
        if (!string.IsNullOrWhiteSpace(taxCode)
            && _settings.TaxIdMappings.TryGetValue(taxCode, out var taxId))
        {
            return taxId;
        }

        return _settings.DefaultTaxId;
    }

    /// <summary>
    /// The rate charged for a SAP VAT group code, from <c>Tax:RatesByTaxCode</c>.
    /// </summary>
    /// <remarks>
    /// The same section the non-fiscal order maths reads, so a rate change lands in one place and the
    /// receipt cannot disagree with the invoice it was built from.
    /// </remarks>
    private decimal ResolveTaxRate(string? taxCode)
        => NormaliseRate(_taxSettings.RateFor(taxCode));

    private static decimal NormaliseRate(decimal rate)
        => rate > 1m ? rate / 100m : rate;

    internal string BuildPreSapInvoiceNo(string externalReference)
    {
        var trimmed = externalReference.Trim();

        return trimmed.All(char.IsAsciiDigit)
            ? _fiscalisationSettings.PreSapInvoiceNoPrefix + trimmed
            : trimmed;
    }

    private static FiscalizationResult Disabled(string invoiceNumber) => new()
    {
        Success = true,
        Skipped = true,
        Message = "Fiscalisation is disabled (Revmax:Enabled is false).",
        InvoiceNumber = invoiceNumber
    };

    private static decimal GetPriceAfterVat(InvoiceLineDto line)
    {
        var grossPrice = Math.Abs(line.GrossPrice);
        return grossPrice > 0m ? grossPrice : Math.Abs(line.UnitPrice);
    }

    /// <summary>The line total declared on the receipt, on the same tax basis as PRICE.</summary>
    /// <remarks>
    /// PRICE is the tax-inclusive unit price, so AMT has to be the tax-inclusive line total or the two
    /// contradict each other on the same receipt line. SAP's <c>LineTotal</c> is NET, so it can only
    /// stand in where the line carries no separate gross price for it to disagree with.
    ///
    /// The pre-2026-08 code took <c>LineTotal</c> whenever it was positive while taking GrossPrice for
    /// PRICE, so on every standard-rated line it declared a net amount beside a gross unit price and
    /// the lines summed short of InvoiceAmount by the VAT.
    /// </remarks>
    private static decimal GetLineAmount(InvoiceLineDto line, decimal quantity, decimal price)
    {
        if (Math.Abs(line.GrossPrice) > 0m)
        {
            return RoundCurrency(quantity * price);
        }

        var lineTotal = Math.Abs(line.LineTotal);
        return lineTotal > 0m ? lineTotal : RoundCurrency(quantity * price);
    }

    private static string? NormalizeTaxCode(string? taxCode)
        => string.IsNullOrWhiteSpace(taxCode) ? null : taxCode.Trim().ToUpperInvariant();

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static decimal RoundCurrency(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string FormatTaxRate(decimal value)
        => Math.Round(value * 100m, 2, MidpointRounding.AwayFromZero)
            .ToString("0.##", CultureInfo.InvariantCulture);

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
}
