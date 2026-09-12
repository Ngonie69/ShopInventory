using System.Diagnostics.CodeAnalysis;
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
/// The path that runs, on the device ZIMRA has actually issued. <see cref="FiscalizationService"/>
/// talks to the in-house Fiscalisation platform — a separate system, and REVMax's intended
/// replacement — which is dormant only because no production device has been issued for it yet. See
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

    /// <summary><c>Data.receiptType</c> on a filed invoice.</summary>
    private const string InvoiceReceiptType = "FiscalInvoice";

    /// <summary><c>Data.receiptType</c> on a filed credit note.</summary>
    private const string CreditNoteReceiptType = "CreditNote";

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

    public async Task<FiscalizationResult> FiscalizeInvoiceAsync(
        InvoiceDto invoice,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var invoiceNumber = invoice.DocNum.ToString(CultureInfo.InvariantCulture);

        if (!_settings.Enabled)
        {
            return Disabled(invoiceNumber);
        }

        var alreadyFiled = await FindFiledReceiptAsync(
            invoiceNumber, InvoiceReceiptType, cancellationToken);

        if (alreadyFiled is not null)
        {
            return alreadyFiled;
        }

        return await FiscaliseInvoiceAsync(
            invoice,
            invoiceNumber,
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
            var mapped = MapResponse(response, invoiceNumber, rawRequestJson);

            if (IsDuplicateInvoiceRefusal(mapped))
            {
                return await AdoptDuplicateReceiptAsync(
                    mapped, invoiceNumber, InvoiceReceiptType, cancellationToken);
            }

            var filed = await ReadBackFiledReceiptAsync(
                mapped, request, InvoiceReceiptType, cancellationToken);

            return await StampFiscalDayAsync(filed, cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return await ReconcileIndeterminateAsync(
                ex, invoiceNumber, InvoiceReceiptType, rawRequestJson, cancellationToken);
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

        var alreadyFiled = await FindFiledReceiptAsync(
            invoiceNumber, CreditNoteReceiptType, cancellationToken);

        if (alreadyFiled is not null)
        {
            return alreadyFiled;
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

        var misalignment = AlignCreditNoteToOriginalReceipt(request, original);

        if (misalignment is not null)
        {
            // Refuse rather than file something ZIMRA will hold against a receipt it does not match.
            return new FiscalizationResult
            {
                Success = false,
                InvoiceNumber = invoiceNumber,
                ErrorCode = "CREDIT_NOTE_NOT_RECONCILED",
                Message = misalignment,
                RawRequestJson = Serialize(request)
            };
        }

        var rawRequestJson = Serialize(request);

        // TransactM files what the apps on this API raise — invoices and the credit notes against
        // them. TransactMExt is for a credit note reversing a receipt ANOTHER device filed, where this
        // device cannot resolve the reference itself and it has to be carried explicitly. (TransactMExt
        // can file an invoice too; nothing here needs that.)
        //
        // The request type is shared and both endpoints take refDeviceId / refReceiptGlobalNo /
        // refFiscalDayNo on the payload, so only the endpoint distinguishes the two cases — which is
        // why sending every credit note to TransactMExt was silent: each one was filed as though it
        // reversed some other device's receipt.
        var originalDeviceId = ParseInt(original.DeviceID);
        var filedOnAnotherDevice =
            originalDeviceId is not null && originalDeviceId != _settings.DefaultRefDeviceId;

        try
        {
            var response = filedOnAnotherDevice
                ? await _client.TransactMExtAsync(request, cancellationToken)
                : await _client.TransactMAsync(request, cancellationToken);

            var mapped = MapResponse(response, invoiceNumber, rawRequestJson);

            if (IsDuplicateInvoiceRefusal(mapped))
            {
                return await AdoptDuplicateReceiptAsync(
                    mapped, invoiceNumber, CreditNoteReceiptType, cancellationToken);
            }

            var filed = await ReadBackFiledReceiptAsync(
                mapped, request, CreditNoteReceiptType, cancellationToken);

            return await StampFiscalDayAsync(filed, cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return await ReconcileIndeterminateAsync(
                ex, invoiceNumber, CreditNoteReceiptType, rawRequestJson, cancellationToken);
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
            // No FiscalDayNo: the lookup does not carry the receipt's day. See AdoptedReceipt.
            DeviceSerial = NullIfBlank(response.DeviceSerialNumber) ?? NullIfBlank(response.Data?.DeviceSerial),
            QRCode = NullIfBlank(response.QRcode),
            VerificationCode = NullIfBlank(response.VerificationCode)
        };
    }

    /// <summary>
    /// Asks the device whether it already holds a receipt for this number, before anything is sent.
    /// </summary>
    /// <remarks>
    /// <c>GET /api/RevmaxAPI/GetInvoice/{invoiceNumber}</c>. It answers Code "1" with the receipt when
    /// it holds one and Code "0" "Invoice not Found" when it does not, so it is a direct answer to
    /// "is this fiscalised?" — the one question our own fiscal transaction log cannot always answer.
    ///
    /// It cannot, because this application is not the only thing filing to this device: the vendor's
    /// own SAP add-on files invoices to it too, leaving no row in our log. Invoice 771485 is one —
    /// receipt 515/216407, filed 2026-09-10 10:05:22 on fiscal day 525, note "22862- From Comex Van
    /// Sales" — while the invoice list still read "Not Fiscalised" and offered a Fiscalise button
    /// that could only ever be refused.
    ///
    /// Answering null when the device cannot be asked is deliberate and safe: this is the cheap path,
    /// not the guard. The post that follows is still covered by the device's own duplicate check, and
    /// <see cref="AdoptDuplicateReceiptAsync"/> turns that refusal into the same answer this would
    /// have given. Contrast <see cref="FindPreSapReceiptAsync"/>, which IS the only guard on a receipt
    /// with no other identity and therefore throws rather than guessing.
    /// </remarks>
    private async Task<FiscalizationResult?> FindFiledReceiptAsync(
        string invoiceNumber,
        string expectedReceiptType,
        CancellationToken cancellationToken)
    {
        InvoiceResponse? existing;

        try
        {
            existing = await _client.GetInvoiceAsync(invoiceNumber, cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Could not ask REVMax whether it already holds a receipt for {InvoiceNumber}. "
                + "Proceeding - the device's own duplicate check still covers this.",
                invoiceNumber);
            return null;
        }

        if (!IsOurReceipt(existing, invoiceNumber, expectedReceiptType))
        {
            return null;
        }

        _logger.LogInformation(
            "REVMax already holds receipt {ReceiptGlobalNo} for {InvoiceNumber}; nothing was sent.",
            existing.Data?.ReceiptGlobalNo,
            invoiceNumber);

        return AdoptedReceipt(existing, invoiceNumber);
    }

    /// <summary>
    /// Whether a receipt the lookup returned is actually ours, and actually the kind we were about to
    /// file.
    /// </summary>
    /// <remarks>
    /// <c>GetInvoice</c> is NOT scoped to our device. This REVMax box serves several, and it answers a
    /// number with whichever device's receipt carries it. Verified 2026-09-10: 700000 comes back as
    /// <c>3180-700000</c> and 50000 as <c>3044-50000</c>, alongside our own <c>22862-771485</c> — and
    /// all three report the same <c>DeviceSerialNumber</c> (<c>8DE6996C0188</c>), so the serial cannot
    /// tell them apart and <c>DeviceID</c> is the only discriminator. Our SAP DocNums run straight
    /// through those ranges, so adopting on "found" alone would mark our invoice fiscalised on the
    /// strength of another taxpayer's receipt, and the real invoice would never be filed.
    ///
    /// The receipt type is checked because one number can hold either kind: REVMax keys a receipt on
    /// its number ("Invoice Numbers should be unique per TIN") and an invoice and a credit note enter
    /// that namespace on equal terms. Adopting an invoice's receipt for a credit note would report the
    /// reversal filed while ZIMRA never saw it.
    ///
    /// Rejecting is cheap: the caller falls through and files, where the device's own per-device
    /// duplicate check still catches a genuine repeat. That asymmetry is why this guard belongs on the
    /// bare-DocNum paths and NOT on <see cref="FindPreSapReceiptAsync"/>, whose number is already
    /// prefixed out of the shared namespace and whose failure mode is signing a second receipt rather
    /// than failing to file a first one.
    /// </remarks>
    private bool IsOurReceipt(
        [NotNullWhen(true)] InvoiceResponse? existing,
        string invoiceNumber,
        string expectedReceiptType)
    {
        if (existing?.Success != true)
        {
            return false;
        }

        var ourDeviceId = _settings.DefaultRefDeviceId.ToString(CultureInfo.InvariantCulture);

        if (!string.Equals(existing.DeviceID, ourDeviceId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "REVMax holds a receipt numbered {InvoiceNumber}, but it belongs to device "
                + "{OwningDeviceId}, not ours ({OurDeviceId}). Ignoring it.",
                invoiceNumber,
                existing.DeviceID,
                ourDeviceId);
            return false;
        }

        var receiptType = existing.Data?.ReceiptType;

        if (!string.Equals(receiptType, expectedReceiptType, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "REVMax holds receipt {InvoiceNumber} on our device, but it is a {ReceiptType} where a "
                + "{ExpectedReceiptType} was expected. Ignoring it.",
                invoiceNumber,
                receiptType ?? "(none)",
                expectedReceiptType);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The receipt REVMax holds, as this application's own result type.
    /// </summary>
    /// <remarks>
    /// Success, because the document is fiscalised and ZIMRA has the receipt — the fields carried here
    /// are what mark it so, and what a reprint puts on the customer's copy.
    ///
    /// No fiscal day. <c>FiscalDay</c> on this response is not the receipt's day: it was taken to be,
    /// because 771485 reads 524 here, but FDMS holds 771485 on day 525. Nor can the device's current day
    /// stand in, because an adopted receipt may have been filed on any day before this one. A blank day
    /// is an omission; the envelope's was a misstatement. See <see cref="StampFiscalDayAsync"/>.
    /// </remarks>
    private static FiscalizationResult AdoptedReceipt(InvoiceResponse existing, string invoiceNumber)
        => new()
        {
            Success = true,
            AlreadyFiscalised = true,
            Message =
                $"Invoice {invoiceNumber} was already fiscalised: REVMax holds receipt "
                + $"{existing.Data?.ReceiptGlobalNo} for it. Nothing was sent again.",
            InvoiceNumber = invoiceNumber,
            ReceiptGlobalNo = existing.Data?.ReceiptGlobalNo.ToString(CultureInfo.InvariantCulture),
            ReceiptCounter = existing.Data?.ReceiptCounter.ToString(CultureInfo.InvariantCulture),
            DeviceSerial = NullIfBlank(existing.DeviceSerialNumber)
                ?? NullIfBlank(existing.Data?.DeviceSerial),
            QRCode = NullIfBlank(existing.QRcode),
            VerificationCode = NullIfBlank(existing.VerificationCode)
        };

    /// <summary>
    /// Whether REVMax refused because it already holds a receipt filed under this invoice number.
    /// </summary>
    /// <remarks>
    /// It arrives as an ordinary refusal — the device answers HTTP 200 to everything, and this one is
    /// Code "0" with the message "Transaction error: Duplicate Invoice Number - The invoice number
    /// (771485) already exists in your Device." There is no distinct code to key on, so the wording is
    /// what there is. Both phrases below are the device's own; matching more loosely than that would
    /// send an ordinary refusal down the adoption path, where a receipt lookup that answered "found"
    /// for the wrong reason would mark an unfiscalised document fiscalised.
    /// </remarks>
    private static bool IsDuplicateInvoiceRefusal(FiscalizationResult result)
        => !result.Success
           && result.Message is { } message
           && (message.Contains("Duplicate Invoice Number", StringComparison.OrdinalIgnoreCase)
               || message.Contains("already exists in your Device", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Turns a duplicate refusal into the receipt REVMax already holds.
    /// </summary>
    /// <remarks>
    /// The refusal is the device saying the document is fiscalised — ZIMRA has the receipt, the
    /// customer was handed it, and nothing was filed by this call. Reporting that as a failure leaves
    /// the invoice reading "Not Fiscalised" with a Fiscalise button on it, which is an invitation to
    /// press it again; it cannot succeed, but a document whose real state nobody can see is how a
    /// second receipt eventually gets signed under some other number.
    ///
    /// So it is resolved the same way <see cref="ReconcileIndeterminateAsync"/> and
    /// <see cref="FindPreSapReceiptAsync"/> resolve theirs: by asking the device what it holds and
    /// adopting it. The tax check is deliberately not run — it compares the filed receipt against
    /// <em>this</em> request's declaration, and the receipt on file was signed from some earlier one.
    ///
    /// If the lookup cannot confirm the receipt, the refusal is returned marked for reconciliation
    /// rather than as a plain failure: the device says a receipt exists, so resubmitting is not a
    /// remedy, and a person has to look.
    /// </remarks>
    private async Task<FiscalizationResult> AdoptDuplicateReceiptAsync(
        FiscalizationResult refusal,
        string invoiceNumber,
        string expectedReceiptType,
        CancellationToken cancellationToken)
    {
        InvoiceResponse? existing = null;

        try
        {
            existing = await _client.GetInvoiceAsync(invoiceNumber, cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                ex,
                "REVMax refused {InvoiceNumber} as a duplicate but could not be asked for the receipt "
                + "it already holds.",
                invoiceNumber);
        }

        // Not merely "was it found". The device said the clash is on OUR device, but the lookup that
        // reads it back is not device-scoped, so a number carried by two devices can hand us the wrong
        // record entirely. Anything we cannot positively identify as ours is a matter for a person.
        if (!IsOurReceipt(existing, invoiceNumber, expectedReceiptType))
        {
            refusal.RequiresReconciliation = true;
            refusal.Message =
                $"REVMax refused {invoiceNumber} because it already holds a receipt under that number, "
                + "but the receipt could not be read back as ours. Look it up before doing anything "
                + $"else — resubmitting cannot help. {refusal.Message}";
            return refusal;
        }

        _logger.LogWarning(
            "REVMax refused {InvoiceNumber} as a duplicate; adopting the receipt it already holds "
            + "(receipt {ReceiptGlobalNo}).",
            invoiceNumber,
            existing.Data?.ReceiptGlobalNo);

        var adopted = AdoptedReceipt(existing, invoiceNumber);

        // Kept for the fiscal transaction log, which records what was sent and what came back. The
        // page that offers the Fiscalise button reads AlreadyFiscalised and shows neither.
        adopted.RawRequestJson = refusal.RawRequestJson;
        adopted.RawResponseJson = refusal.RawResponseJson;
        return adopted;
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
        string expectedReceiptType,
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

            // Ours, and the right kind. A number this device did not file is not evidence of anything.
            if (IsOurReceipt(existing, invoiceNumber, expectedReceiptType))
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
                    // No FiscalDayNo: this receipt may predate the call. See AdoptedReceipt.
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

    /// <summary>
    /// Reads the filed receipt back: takes its receipt counter, and checks the device taxed each line
    /// the way we declared it.
    /// </summary>
    /// <remarks>
    /// The one failure this integration cannot otherwise see. REVMax answers a submission with
    /// "Upload Success" and no tax breakdown at all, so a wrong <c>TAX</c> id is invisible at the
    /// point of sending: the receipt is filed, the customer is handed it, and the VAT declared to
    /// ZIMRA is simply wrong. That is not hypothetical on this device — the feed already writing to
    /// it declared SAP invoice 771225 (VatSum 0.00, wholly zero-rated) with 0.27 of VAT, and 771191
    /// with 2.15 against SAP's 1.47, because it filed one of two zero-rated lines at 15.5%. Neither
    /// raised anything anywhere.
    ///
    /// Never changes <see cref="FiscalizationResult.Success"/> and never asks for a retry. The
    /// receipt exists; resubmitting would add a second one. It raises
    /// <see cref="FiscalizationResult.TaxDeclarationMismatch"/> so a person can decide, which for a
    /// filed receipt usually means a credit note.
    ///
    /// Best-effort: a read-back that cannot be made says nothing about the receipt, so it is logged
    /// and passed over rather than reported as a mismatch.
    ///
    /// The receipt counter is taken here because this read-back is the only place it appears — see
    /// <see cref="ApplyFiledReceiptNumbers"/>.
    /// </remarks>
    private async Task<FiscalizationResult> ReadBackFiledReceiptAsync(
        FiscalizationResult result,
        TransactMRequest request,
        string expectedReceiptType,
        CancellationToken cancellationToken)
    {
        if (!result.Success || result.Skipped || result.InvoiceNumber is null)
        {
            return result;
        }

        InvoiceResponse? filed;

        try
        {
            filed = await _client.GetInvoiceAsync(result.InvoiceNumber, cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Filed {InvoiceNumber} but could not read it back to take its receipt counter or check "
                + "the tax it was recorded under.",
                result.InvoiceNumber);
            return result;
        }

        ApplyFiledReceiptNumbers(result, filed, expectedReceiptType);

        if (request.ItemsXml is not List<RevmaxRequestItem> declared || declared.Count == 0)
        {
            return result;
        }

        var recorded = filed?.Data?.ReceiptLines;

        if (filed?.Success != true || recorded is null || recorded.Count == 0)
        {
            _logger.LogWarning(
                "Filed {InvoiceNumber} but the read-back carried no lines to check the tax against.",
                result.InvoiceNumber);
            return result;
        }

        var mismatches = new List<string>();

        foreach (var item in declared)
        {
            // HH is the line number we sent and receiptLineNo is what came back on it.
            if (!int.TryParse(item.HH, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNo))
            {
                continue;
            }

            var line = recorded.FirstOrDefault(r => r.ReceiptLineNo == lineNo);

            if (line is null
                || !decimal.TryParse(
                    item.TaxR, NumberStyles.Number, CultureInfo.InvariantCulture, out var declaredRate))
            {
                continue;
            }

            if (declaredRate == line.TaxPercent)
            {
                continue;
            }

            mismatches.Add(
                $"line {lineNo} ({item.ItemCode}) declared at {declaredRate}% but recorded at "
                + $"{line.TaxPercent}% (taxID {line.TaxID})");
        }

        if (mismatches.Count == 0)
        {
            return result;
        }

        var detail = string.Join("; ", mismatches);

        _logger.LogError(
            "Receipt {ReceiptGlobalNo} for {InvoiceNumber} is filed with ZIMRA declaring tax this "
            + "application did not intend, so the VAT reported is wrong and cannot be corrected on "
            + "the receipt: {Detail}. Check Revmax:TaxIdMappings against the device.",
            filed.Data?.ReceiptGlobalNo,
            result.InvoiceNumber,
            detail);

        result.TaxDeclarationMismatch = true;
        result.TaxDeclarationDetail = detail;
        result.Message = $"{result.Message} The tax declared does not match the receipt: {detail}.";

        return result;
    }

    /// <summary>
    /// Takes the receipt counter from the read-back, and the global number too where the filing's QR
    /// code did not carry one — but only when the read-back is this filing's receipt.
    /// </summary>
    /// <remarks>
    /// TransactM's own body carries neither number: till sale GRC-FAC-20260911-286EEC7389FD came back
    /// without <c>ReceiptGlobalNo</c>, so every till sale recorded a blank receipt number. The
    /// read-back has both, but <c>GetInvoice</c> is not device-scoped (see <see cref="IsOurReceipt"/>),
    /// so it is matched on the global number in the QR code, or failing a QR code, on our device and
    /// the receipt type.
    /// </remarks>
    private void ApplyFiledReceiptNumbers(
        FiscalizationResult result,
        InvoiceResponse? filed,
        string expectedReceiptType)
    {
        if (filed?.Success != true || filed.Data is not { ReceiptGlobalNo: > 0 } receipt)
        {
            return;
        }

        var filedGlobalNo = receipt.ReceiptGlobalNo.ToString(CultureInfo.InvariantCulture);
        var qrGlobalNo = ReceiptGlobalNoFromQrCode(result.QRCode);

        var isThisFiling = qrGlobalNo is not null
            ? qrGlobalNo == filedGlobalNo
            : string.Equals(
                  filed.DeviceID,
                  _settings.DefaultRefDeviceId.ToString(CultureInfo.InvariantCulture),
                  StringComparison.Ordinal)
              && string.Equals(receipt.ReceiptType, expectedReceiptType, StringComparison.OrdinalIgnoreCase);

        if (!isThisFiling)
        {
            return;
        }

        result.ReceiptGlobalNo = filedGlobalNo;

        if (receipt.ReceiptCounter > 0)
        {
            result.ReceiptCounter = receipt.ReceiptCounter.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Sets the fiscal day a receipt this call has just filed went into, when the device can vouch for
    /// it.
    /// </summary>
    /// <remarks>
    /// Never from a REVMax envelope. <c>FiscalDay</c> rides on every response and is not the receipt's
    /// day: on 2026-09-11 TransactM, GetInvoice and GetDayStatus all read 524, while FDMS holds till
    /// sale GRC-FAC-20260911-286EEC7389FD as 985/216877 on day 525 — and 771485, filed the day before,
    /// as 515/216407 on day 525 too. Copying it printed the wrong day on every till receipt.
    ///
    /// GetDayStatus's <c>lastFiscalDayNo</c> is ZIMRA's, and read 525 — but it is the device's day now,
    /// not the receipt's. It is taken as the receipt's only when nothing can have come between: FDMS
    /// already holds this receipt (<c>lastReceiptGlobalNo</c> has reached it), and that day is still
    /// open — <c>FiscalDayOpened</c>, or <c>FiscalDayCloseFailed</c>, which leaves it open. A different
    /// day would need this one closed and the next opened in the moments since the device answered, and
    /// a close under way reads <c>FiscalDayCloseInitiated</c>, which is refused.
    ///
    /// Anything short of that leaves the day blank and the receipt still a success: a blank day is an
    /// omission, a wrong one is a misstatement on a document that cannot be amended. Receipts this call
    /// did not file — adopted, or found after a failed call — are never stamped, because they may be
    /// from any earlier day. The cost is one more call per filing, about 1.7s, since the device asks
    /// FDMS.
    /// </remarks>
    private async Task<FiscalizationResult> StampFiscalDayAsync(
        FiscalizationResult result,
        CancellationToken cancellationToken)
    {
        if (!result.Success
            || result.Skipped
            || result.AlreadyFiscalised
            || !long.TryParse(
                result.ReceiptGlobalNo, NumberStyles.None, CultureInfo.InvariantCulture, out var receiptGlobalNo))
        {
            return result;
        }

        DayStatusResponse? status;

        try
        {
            status = await _client.GetDayStatusAsync(cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Filed receipt {ReceiptGlobalNo} for {InvoiceNumber} but could not read the device's day "
                + "status, so its fiscal day is left blank.",
                receiptGlobalNo,
                result.InvoiceNumber);
            return result;
        }

        var day = status is { Code: "1" } ? status.Data : null;

        if (day is { LastFiscalDayNo: > 0, FiscalDayStatus: "FiscalDayOpened" or "FiscalDayCloseFailed" }
            && day.LastReceiptGlobalNo >= receiptGlobalNo)
        {
            result.FiscalDayNo = day.LastFiscalDayNo.ToString(CultureInfo.InvariantCulture);
            return result;
        }

        _logger.LogWarning(
            "Filed receipt {ReceiptGlobalNo} for {InvoiceNumber}, but the device's day status cannot show "
            + "which fiscal day it went into (Code {Code}, {FiscalDayStatus}, day {LastFiscalDayNo}, last "
            + "receipt {LastReceiptGlobalNo}), so its fiscal day is left blank.",
            receiptGlobalNo,
            result.InvoiceNumber,
            status?.Code,
            status?.Data?.FiscalDayStatus,
            status?.Data?.LastFiscalDayNo,
            status?.Data?.LastReceiptGlobalNo);
        return result;
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
            // No FiscalDayNo: nothing on this body is the receipt's day. It is stamped once the
            // device's day status can vouch for one — see StampFiscalDayAsync.
            //
            // No receipt number on the body either (till receipt 216877 came back without one), but
            // the QR code is here, and it carries the number.
            ReceiptGlobalNo = ReceiptGlobalNoFromQrCode(response.QRcode) ?? NullIfBlank(response.ReceiptGlobalNo),
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
        request.InvoiceComment = ResolveComment(invoice, $"Invoice {invoiceNumber}");
        return request;
    }

    /// <summary>
    /// Brings a credit note into line with the receipt it reverses, before anything is sent.
    /// </summary>
    /// <remarks>
    /// Three corrections, all of them things the device or ZIMRA measures against the ORIGINAL
    /// receipt rather than against SAP. Returns null when the credit note is fit to send, or the
    /// reason it is not.
    /// </remarks>
    private string? AlignCreditNoteToOriginalReceipt(
        TransactMExtRequest request,
        InvoiceResponse original)
    {
        if (request.ItemsXml is not List<RevmaxRequestItem> items || items.Count == 0)
        {
            return null;
        }

        ApplyOriginalReceiptTaxes(items, original);
        return CapAndReconcileCreditNote(request, items, original);
    }

    /// <summary>
    /// Declares each line under the tax the ORIGINAL receipt declared it under.
    /// </summary>
    /// <remarks>
    /// Tax ids are configured per taxpayer on the device, so the receipt being reversed — not our
    /// <c>Revmax:TaxIdMappings</c> — is the authority on which id stands for which rate. Reversing a
    /// line under a different tax id from the one it was sold under credits the wrong tax, and neither
    /// receipt can be amended afterwards.
    ///
    /// Lines are matched by name, which is what the receipt carries. A line that does not match — an
    /// edited description, or free text — keeps the rate its own VAT group gives it, but borrows the
    /// receipt's id for that rate, so one credit note never mixes id schemes.
    /// </remarks>
    private static void ApplyOriginalReceiptTaxes(
        List<RevmaxRequestItem> items,
        InvoiceResponse original)
    {
        var receiptLines = original.Data?.ReceiptLines;

        if (receiptLines is null || receiptLines.Count == 0)
        {
            return;
        }

        var byName = receiptLines
            .Where(line => !string.IsNullOrWhiteSpace(line.ReceiptLineName))
            .GroupBy(line => line.ReceiptLineName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var idByRate = receiptLines
            .GroupBy(line => line.TaxPercent)
            .ToDictionary(group => group.Key, group => group.First().TaxID);

        foreach (var item in items)
        {
            if (byName.TryGetValue(item.ItemName1 ?? string.Empty, out var matched))
            {
                item.Tax = matched.TaxID.ToString(CultureInfo.InvariantCulture);
                item.TaxR = FormatTaxRate(NormaliseRate(matched.TaxPercent));
                continue;
            }

            // TAXR is already a percentage here, and so is the receipt's TaxPercent.
            if (idByRate.TryGetValue(ParseAmount(item.TaxR), out var taxId))
            {
                item.Tax = taxId.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// Caps the credit note at the original receipt's total and makes the lines sum to it exactly.
    /// </summary>
    /// <remarks>
    /// REVMax measures a credit note against the ORIGINAL RECEIPT's total, not against SAP's document
    /// total. The two are rounded by different systems and SAP can land a cent above, which the device
    /// refuses outright: "Credit Note Amount X exceeds the original Invoice Amount Y". Capped, never
    /// raised — raising it would turn a partial credit note into a full one.
    ///
    /// The lines then have to sum to that figure, because **the device recomputes every line total
    /// from QTY x PRICE and discards the AMT it was sent** (proven on invoice 769617, whose last line
    /// went out at 1.91 and was stored as 1.89). So the residual is moved onto the largest line's
    /// PRICE, not just its AMT — adjusting AMT alone changes nothing at the device, and ZIMRA's
    /// RCPT019 rejects a receipt whose declared total differs from the sum of its lines.
    ///
    /// Bounded at about a cent per line, which is all that independent per-line rounding can produce.
    /// A larger gap is missing money — freight, a discount, a rounding row — and absorbing it would
    /// misstate a line on a document that cannot be amended, so it is refused instead.
    /// </remarks>
    private static string? CapAndReconcileCreditNote(
        TransactMExtRequest request,
        List<RevmaxRequestItem> items,
        InvoiceResponse original)
    {
        var receiptTotal = RoundCurrency(Math.Abs(original.Data?.ReceiptTotal ?? 0m));
        var target = receiptTotal > 0m
            ? Math.Min(request.InvoiceAmount, receiptTotal)
            : request.InvoiceAmount;

        var lineTotals = DeviceLineTotals(items);
        var difference = RoundCurrency(target - lineTotals.Sum());

        if (difference != 0m)
        {
            var tolerance = 0.01m * items.Count + 0.01m;

            if (Math.Abs(difference) > tolerance)
            {
                return
                    $"Credit note {request.InvoiceNumber}: the lines total "
                    + $"{FormatMoney(lineTotals.Sum())} but it must be credited at {FormatMoney(target)}. "
                    + "The gap is too large to be rounding — check the document in SAP for freight, "
                    + "discount or rounding amounts that are not line items.";
            }

            if (!SpreadResidualOntoLargestLine(items, lineTotals, difference))
            {
                return
                    $"Credit note {request.InvoiceNumber}: the lines miss the credited total by "
                    + $"{FormatMoney(difference)} and the largest line has no quantity to spread it "
                    + "over.";
            }
        }

        request.InvoiceAmount = target;

        if (request.CurrenciesXml is List<RevmaxRequestCurrency> currencies)
        {
            foreach (var currency in currencies)
            {
                currency.Amount = FormatMoney(target);
            }
        }

        return null;
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
        request.InvoiceComment = ResolveComment(
            creditNote, $"Credit note {invoiceNumber} against invoice {originalInvoiceNumber}");

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
        // Empty string, never null. The serialiser omits a null property altogether and the device
        // dereferences these without checking: an absent CustomerVatNumber comes back as HTTP 200
        // carrying Code "0" and "Object reference not set to an instance of an object", which reads
        // like a fault on their side and is a missing field on ours. Its whole contract is strings.
        request.CustomerName = Text(customerDetails?.CustomerName ?? document.CardName);
        request.CustomerVatNumber = Text(customerDetails?.VatNumber ?? document.CustomerVatNo);
        request.CustomerAddress = Text(customerDetails?.Address ?? document.BillToAddress);
        request.CustomerTelephone = Text(customerDetails?.Telephone ?? document.CustomerPhone);
        request.CustomerEmail = Text(customerDetails?.Email ?? document.CustomerEmail);
        request.CustomerBPN = Text(customerDetails?.BPN ?? document.CustomerTinNumber);

        // Credit notes come off SAP negative; REVMax is told the magnitude and Istatus "02" carries the
        // sign.
        request.InvoiceAmount = RoundCurrency(Math.Abs(document.DocTotal));
        request.InvoiceTaxAmount = RoundCurrency(Math.Abs(document.VatSum));
        request.Cashier = Text(document.CardCode);
        request.ItemsXml = BuildItems(document);
        request.CurrenciesXml = BuildCurrencies(document);
    }

    private List<RevmaxRequestItem> BuildItems(InvoiceDto document)
    {
        if (document.Lines is null || document.Lines.Count == 0)
        {
            return new List<RevmaxRequestItem>();
        }

        var items = document.Lines.Select(line =>
        {
            var quantity = Math.Abs(line.Quantity);

            // Rounded BEFORE the amount is derived from it. PRICE goes on the wire at two decimals and
            // the device recomputes the line from what it was sent, so deriving AMT from the
            // full-precision price put the two out of step on any price SAP did not already hold at two
            // decimals: 3 x 1.115 declared 3.35 against a receipt line the device stored as 3.36.
            var price = RoundCurrency(GetPriceAfterVat(line));
            var amount = GetLineAmount(line, quantity, price);
            var description = line.ItemDescription ?? string.Empty;
            var taxCode = NormalizeTaxCode(line);

            return new RevmaxRequestItem
            {
                HH = line.LineNum.ToString(CultureInfo.InvariantCulture),
                ItemCode = line.ItemCode ?? string.Empty,
                ItemName1 = description,
                ItemName2 = description,
                Qty = quantity.ToString(CultureInfo.InvariantCulture),
                Price = FormatMoney(price),
                Amt = FormatMoney(amount),
                Tax = ResolveTaxId(taxCode).ToString(CultureInfo.InvariantCulture),
                TaxR = FormatTaxRate(ResolveTaxRate(taxCode))
            };
        }).ToList();

        ReconcileToDocumentTotal(items, RoundCurrency(Math.Abs(document.DocTotal)));

        return items;
    }

    /// <summary>
    /// Absorbs sub-cent rounding so the filed receipt adds up to the invoice total.
    /// </summary>
    /// <remarks>
    /// Each unit price is rounded to the cent before it is multiplied out, so across a document of any
    /// size the lines can miss the total by a cent or two — invoice 769617 lands 0.02 short over nine
    /// lines. A tax document whose lines do not sum to its own total invites a query nobody can answer
    /// afterwards, and the receipt cannot be amended.
    ///
    /// The correction goes on PRICE, because that is the only field the device reads: it recomputes
    /// every line total from QTY x PRICE and discards the AMT it was sent — 769617's last line went
    /// out at 1.91 and was stored as 1.89. This used to move AMT on the last line and leave PRICE
    /// alone, on the reasoning that PRICE is a published unit price. That reasoning predates the
    /// discovery above: moving AMT alone changed nothing on the receipt and left our own record
    /// disagreeing with the copy in the customer's hand, which is the misstatement it was trying to
    /// avoid. Carrying it on PRICE moves a real unit price by a fraction of a cent and makes the
    /// receipt total right.
    ///
    /// Bounded deliberately at ten cents. Anything larger is not rounding — it is a wrong price, a
    /// missed discount or a line the mapping dropped — and quietly papering over it would file the
    /// wrong receipt while making it look right. Those are left visibly unbalanced instead: unlike a
    /// credit note, whose figure is measured against the original receipt and is refused outright, an
    /// invoice that cannot be reconciled is still better filed than not filed at all — 769617 went
    /// through two cents short.
    /// </remarks>
    private static void ReconcileToDocumentTotal(List<RevmaxRequestItem> items, decimal documentTotal)
    {
        if (items.Count == 0 || documentTotal <= 0m)
        {
            return;
        }

        var lineTotals = DeviceLineTotals(items);
        var difference = RoundCurrency(documentTotal - lineTotals.Sum());

        if (difference == 0m || Math.Abs(difference) > 0.10m)
        {
            return;
        }

        SpreadResidualOntoLargestLine(items, lineTotals, difference);
    }

    /// <summary>What the DEVICE will make of each line: QTY x PRICE, not the AMT we sent it.</summary>
    private static List<decimal> DeviceLineTotals(List<RevmaxRequestItem> items)
        => items
            .Select(item => RoundCurrency(ParseAmount(item.Qty) * ParseAmount(item.Price)))
            .ToList();

    /// <summary>
    /// Moves a rounding residual onto the largest line, through PRICE, keeping AMT equal to
    /// QTY x PRICE.
    /// </summary>
    /// <remarks>
    /// The largest line carries it so the per-unit change is the smallest available. Answers false
    /// when that line has no quantity to spread it over, which leaves the caller to decide whether an
    /// unreconciled document may still be filed.
    /// </remarks>
    private static bool SpreadResidualOntoLargestLine(
        List<RevmaxRequestItem> items,
        IReadOnlyList<decimal> lineTotals,
        decimal difference)
    {
        var index = 0;

        for (var i = 1; i < lineTotals.Count; i++)
        {
            if (Math.Abs(lineTotals[i]) > Math.Abs(lineTotals[index]))
            {
                index = i;
            }
        }

        var quantity = ParseAmount(items[index].Qty);

        if (quantity == 0m)
        {
            return false;
        }

        var adjusted = RoundCurrency(lineTotals[index] + difference);
        items[index].Amt = FormatMoney(adjusted);
        items[index].Price = FormatUnitPrice(adjusted / quantity);
        return true;
    }

    private static decimal ParseAmount(string? value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;

    private List<RevmaxRequestCurrency> BuildCurrencies(InvoiceDto document)
        => new()
        {
            new RevmaxRequestCurrency
            {
                Name = document.DocCurrency ?? _settings.DefaultCurrency,
                Amount = FormatMoney(Math.Abs(document.DocTotal)),
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
        => _fiscalisationSettings.BuildPreSapInvoiceNo(externalReference);

    private static FiscalizationResult Disabled(string invoiceNumber) => new()
    {
        Success = true,
        Skipped = true,
        Message = "Fiscalisation is disabled (Revmax:Enabled is false).",
        InvoiceNumber = invoiceNumber
    };

    /// <summary>The gross unit price to declare: what the customer actually paid, per unit.</summary>
    /// <remarks>
    /// <c>PriceAfterVat</c> first, because it is the only one of these that is both gross AND after the
    /// line discount. SAP's <c>GrossPrice</c> comes off the price list and ignores the discount
    /// entirely — on invoice 769617, half of it, which would have declared 845.26 to ZIMRA against a
    /// real invoice total of 422.89. <c>GrossPrice</c> is kept only as the fallback for the pre-SAP
    /// path, where <c>DesktopSaleFiscaliser</c> computes it from the effective price and no
    /// PriceAfterVat exists.
    /// </remarks>
    private static decimal GetPriceAfterVat(InvoiceLineDto line)
    {
        var priceAfterVat = Math.Abs(line.PriceAfterVat);
        if (priceAfterVat > 0m)
        {
            return priceAfterVat;
        }

        var grossPrice = Math.Abs(line.GrossPrice);
        return grossPrice > 0m ? grossPrice : Math.Abs(line.UnitPrice);
    }

    /// <summary>The line total declared on the receipt, on the same tax basis as PRICE.</summary>
    /// <remarks>
    /// Always QTY x PRICE, because that is what the device itself records: REVMax recomputes every
    /// line total from quantity and price and discards the AMT it was sent. Verified on invoice
    /// 769617, whose last line was submitted as 1.91 and stored as 1.89. Sending anything else only
    /// makes our own record disagree with the receipt the customer is holding.
    ///
    /// The same goes for the document total. REVMax derived 422.87 for 769617 from its own lines and
    /// ignored the InvoiceAmount of 422.89 it was sent - two cents of rounding accumulated across
    /// nine lines. It cannot be closed without misstating a unit price, and the device's existing
    /// feed drifts by a cent in both directions on ordinary till invoices anyway.
    /// </remarks>
    private static decimal GetLineAmount(InvoiceLineDto line, decimal quantity, decimal price)
        => RoundCurrency(quantity * price);

    /// <summary>The SAP tax code for a line, from wherever SAP actually put it.</summary>
    /// <remarks>
    /// On a marketing document line SAP returns <c>TaxCode</c> null and puts the code in
    /// <c>VatGroup</c>. Reading TaxCode alone matches nothing in the mappings, so every line falls to
    /// the standard-rated default — which declares a zero-rated line to ZIMRA at 15.5%.
    /// </remarks>
    private static string? NormalizeTaxCode(InvoiceLineDto line)
    {
        var code = string.IsNullOrWhiteSpace(line.TaxCode) ? line.VatGroup : line.TaxCode;
        return string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
    }

    /// <summary>The receipt's free-text comment, which the device will not accept empty.</summary>
    /// <remarks>
    /// REVMax refuses a submission whose InvoiceComment is blank - "InvoiceComment is null or empty",
    /// returned as HTTP 200 carrying Code "0". It is not marked required anywhere in the device's
    /// Swagger, and plenty of SAP documents carry no Comments at all: invoice 769617 has an empty
    /// string, so passing Comments straight through refused it. The fallback names the document
    /// rather than inventing narrative, because this prints on the customer's fiscal receipt.
    /// </remarks>
    private static string ResolveComment(InvoiceDto document, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(document.Comments))
        {
            return document.Comments.Trim();
        }

        return string.IsNullOrWhiteSpace(document.Remarks) ? fallback : document.Remarks.Trim();
    }

    /// <summary>A string the device can dereference: never null, always trimmed.</summary>
    private static string Text(string? value) => value?.Trim() ?? string.Empty;

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>The receipt global number a ZIMRA verification QR code carries, if it is one.</summary>
    /// <remarks>
    /// The code is the portal URL followed by the device id (10 digits), the receipt date (ddMMyyyy),
    /// the receipt global number (10 digits) and 16 hex characters of signature. Till sale
    /// GRC-FAC-20260911-286EEC7389FD's reads 0000022862 11092026 0000216877 60A74CD961202377, and FDMS
    /// shows that receipt as 216877. Anything not shaped like that answers null.
    /// </remarks>
    private static string? ReceiptGlobalNoFromQrCode(string? qrCode)
    {
        if (string.IsNullOrWhiteSpace(qrCode))
        {
            return null;
        }

        var trimmed = qrCode.Trim().TrimEnd('/');
        var code = trimmed[(trimmed.LastIndexOf('/') + 1)..];

        if (code.Length != 44
            || !code[..28].All(char.IsAsciiDigit)
            || !code[28..].All(char.IsAsciiHexDigit))
        {
            return null;
        }

        return long.TryParse(code.AsSpan(18, 10), NumberStyles.None, CultureInfo.InvariantCulture, out var globalNo)
               && globalNo > 0
            ? globalNo.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Money as the device expects it: always two decimals.</summary>
    /// <remarks>
    /// Explicit, because <see cref="Math.Round(decimal, int)"/> preserves the operand's scale rather
    /// than imposing one — a line total of 100 serialised as "100" beside a neighbour's "97.98".
    /// </remarks>
    private static string FormatMoney(decimal value)
        => RoundCurrency(value).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A unit price the device can multiply back to the exact line amount.</summary>
    /// <remarks>
    /// Two decimals cannot always express amount / quantity, and the device recomputes the line from
    /// QTY x PRICE, so a 2dp price would reintroduce the residual this is here to remove.
    /// </remarks>
    private static string FormatUnitPrice(decimal value)
        => Math.Round(value, 6, MidpointRounding.AwayFromZero)
            .ToString("0.00####", CultureInfo.InvariantCulture);

    private static decimal RoundCurrency(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string FormatTaxRate(decimal value)
        => Math.Round(value * 100m, 2, MidpointRounding.AwayFromZero)
            .ToString("0.##", CultureInfo.InvariantCulture);

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
}
