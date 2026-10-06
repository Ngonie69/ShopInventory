using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
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
    /// <remarks>
    /// <paramref name="externalReference"/> is the stable identifier for the document. It becomes the
    /// receipt's permanent fiscal identity and part of the platform's idempotency key, so it must be
    /// identical on every retry and must never be regenerated.
    ///
    /// <paramref name="paymentType"/> is the money type declared to ZIMRA. Pass the sale's own tender,
    /// mapped through <see cref="ShopInventory.Common.Sales.TenderTypes.ToMoneyType"/>. A caller with
    /// no tender to declare passes null and the receipt falls back to cash — which is only right for a
    /// till that takes nothing else, so pass the real tender wherever one was captured.
    ///
    /// <paramref name="printForm"/> is the fiscal document the receipt is filed as. Till, vending and van
    /// sales take the partner's choice from <see cref="Features.FiscalPrintForms.IFiscalPrintFormResolver"/>;
    /// everything else is an A4 invoice.
    ///
    /// <paramref name="source"/> is the channel and warehouse the sale was raised in, which the platform
    /// shows as the receipt's origin. It is not part of the receipt.
    /// </remarks>
    Task<FiscalizationResult> FiscalizePreSapInvoiceAsync(
        InvoiceDto invoice,
        string externalReference,
        CustomerFiscalDetails? customerDetails = null,
        MoneyType? paymentType = null,
        ReceiptPrintForm printForm = ReceiptPrintForm.InvoiceA4,
        FiscalReceiptSource? source = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fiscalises a credit note that has already been posted to SAP.
    /// </summary>
    /// <remarks>
    /// <paramref name="originalInvoiceNumber"/> is the invoice being credited. Advisory only — recorded
    /// for traceability. The platform recovers the fiscal link itself from the SAP document's
    /// BaseType/BaseEntry, across all devices, which is strictly better than anything this app can
    /// compute.
    /// </remarks>
    Task<FiscalizationResult> FiscalizeCreditNoteAsync(
        InvoiceDto creditNote,
        string originalInvoiceNumber,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a document has already been fiscalised, on any device.
    /// </summary>
    Task<bool> IsInvoiceFiscalizedAsync(string invoiceNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks for a receipt an earlier attempt may have signed for this sale, so it can be adopted
    /// rather than signed again.
    /// </summary>
    /// <remarks>
    /// For the case where a submission was made but its outcome never reached us — the process died,
    /// the save failed, the connection dropped. Resubmitting then risks a second fiscal receipt, which
    /// cannot be withdrawn.
    ///
    /// Returns null only when the platform positively says there is no such receipt. If it cannot be
    /// asked, this THROWS: treating "I could not check" as "there is nothing there" is exactly how the
    /// duplicate gets signed. <see cref="IsInvoiceFiscalizedAsync"/> answers false in that case, which
    /// is why it is not used here.
    /// </remarks>
    Task<FiscalizationResult?> FindPreSapReceiptAsync(
        string externalReference,
        CancellationToken cancellationToken = default);
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
    /// The receipt already existed at the fiscal device, so this attempt filed nothing new and the
    /// fiscal details on this result were adopted from the receipt already on file.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Skipped"/>, which also covers fiscalisation being switched off and a
    /// server-side dry run. This one says a receipt exists at ZIMRA for the document: it is a success
    /// for the document, and it must never be retried.
    /// </remarks>
    public bool AlreadyFiscalised { get; set; }

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

    /// <summary>
    /// The receipt was filed, but the tax the device recorded is not the tax that was declared.
    /// </summary>
    /// <remarks>
    /// A filed receipt cannot be corrected, so this is never a failure and must never trigger a retry
    /// — the sale is with ZIMRA either way, and resubmitting would only add a second receipt. It says
    /// the amount of VAT declared to ZIMRA is wrong, which is a matter for a person and usually a
    /// credit note.
    ///
    /// It exists because the failure is otherwise completely silent. The device feeding this same
    /// REVMax already declares zero-rated goods at 15.5%: on 2026-09-09, SAP invoice 771225 was
    /// wholly zero-rated (VatSum 0.00) and its receipt declared 0.27 of VAT, and 771191 declared 2.15
    /// against SAP's 1.47 because one of its two zero-rated lines was filed standard-rated. Nothing
    /// anywhere reported either. <see cref="TaxDeclarationDetail"/> carries what differed.
    /// </remarks>
    public bool TaxDeclarationMismatch { get; set; }

    /// <summary>Which lines were declared at a rate the device did not record.</summary>
    public string? TaxDeclarationDetail { get; set; }

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
    private readonly TaxSettings _tax;
    private readonly ILogger<FiscalizationService> _logger;
    private readonly IItemHsCodes? _itemHsCodes;

    public FiscalizationService(
        IFiscalisationApiClient client,
        IFiscalDeviceConfigCache configCache,
        IOptions<FiscalisationSettings> settings,
        IOptions<TaxSettings> taxSettings,
        ILogger<FiscalizationService> logger,
        IItemHsCodes? itemHsCodes = null)
    {
        _itemHsCodes = itemHsCodes;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _configCache = configCache ?? throw new ArgumentNullException(nameof(configCache));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _tax = taxSettings?.Value ?? throw new ArgumentNullException(nameof(taxSettings));
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

        // No lines and no currency: the platform reads both from SAP.
        //
        // The invoice number is the one thing worth overriding, and only when the mobile invoice-number
        // UDF is configured. Left unset, the platform keys the receipt on the SAP DocNum — which is a
        // different string from the reference the same sale was stamped under on a handset, so one sale
        // becomes two receipts at ZIMRA under two numbers, and the platform's (taxpayer, type, number)
        // idempotency cannot see that they are the same. Sending the UDF's value makes both paths agree.
        var mobileInvoiceNo = ResolveMobileInvoiceNo(document);

        var request = new SapFiscaliseReceiptApiRequest
        {
            SapDocument = new SapDocumentReference
            {
                DocumentType = documentType,
                DocEntry = document.DocEntry
            },
            Receipt = new SubmitReceiptApiRequest
            {
                // Which of the platform's devices may take this receipt.
                //
                // Zero means "walk every configured device until one accepts", which is what we want for
                // failover — but it must never reach a device a van handset signs on. Those are
                // registered with ZIMRA in Offline mode and their sequence belongs to the handset; a
                // server-signed receipt on one forks its chain and voids the whole fiscal day. The
                // platform refuses a pre-signed receipt for an Online device and vice versa, but nothing
                // stops it walking onto a handset's device from here, so the pin is configured on our
                // side: set Fiscalisation:DefaultDeviceId to an Online device on any deployment whose
                // handsets own devices.
                DeviceId = _settings.DefaultDeviceId,
                InvoiceNo = mobileInvoiceNo,
                ReceiptType = receiptType,
                Buyer = MapBuyer(customerDetails),
                ReceiptNotes = document.Comments
            }
        };

        // Everything that reaches this path was raised in ShopInventory and posted to SAP first, so the
        // platform is told so; left unstated it would read as a document the SAP bridge sent.
        FiscalReceiptSource.ForSapDocument(
                documentType == SapDocumentType.CreditNote ? FiscalReceiptSource.SalesCreditNote : FiscalReceiptSource.SalesInvoice,
                document.Lines?.Select(line => line.WarehouseCode) ?? [])
            .ApplyTo(request.Receipt);

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
        MoneyType? paymentType = null,
        ReceiptPrintForm printForm = ReceiptPrintForm.InvoiceA4,
        FiscalReceiptSource? source = null,
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

        var lines = await BuildPreSapLinesAsync(invoice, cancellationToken);
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
            // The receipt declares to ZIMRA how the customer paid, so it follows the sale's tender.
            // Cash is the fallback for a caller that captured none — historically every caller, which
            // is why this was a constant.
            PaymentType = paymentType ?? MoneyType.Cash,
            // The amount actually taken, when the caller knows it. Re-deriving it from the line prices
            // declares a different number: a line price is per unit and rounded to the cent, while the
            // sale rounds tax once over the whole line, so multiplying the rounded unit price back out
            // drifts by up to half a cent per unit — 100 x $1.99 is charged $229.85 and would be
            // declared $230.00. The customer paid one specific amount and that is what the receipt has
            // to say; the per-line breakdown stays in cents, as a printed receipt must.
            PaymentAmount = invoice.DocTotal > 0m
                ? RoundCurrency(invoice.DocTotal)
                : lines.Sum(line => RoundCurrency(line.Price * line.Quantity)),
            Lines = lines,
            Buyer = MapBuyer(customerDetails),
            ReceiptNotes = invoice.Comments,
            ReceiptPrintForm = printForm
        };
        source?.ApplyTo(request);

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

    public async Task<FiscalizationResult?> FindPreSapReceiptAsync(
        string externalReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalReference) || !_settings.Enabled)
        {
            return null;
        }

        var invoiceNo = BuildPreSapInvoiceNo(externalReference);

        CheckFiscalisedReceiptApiResponse response;
        try
        {
            // Device 0 searches every device: an earlier attempt may have failed over.
            response = await _client.CheckReceiptAsync(
                0, invoiceNo, ReceiptType.FiscalInvoice, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Not fiscalising {invoiceNo}: the platform could not be asked whether it already holds "
                + $"this receipt. {ex.Message}", ex);
        }

        if (!response.IsFiscalised)
        {
            return null;
        }

        var match = response.Matches.FirstOrDefault();

        _logger.LogWarning(
            "Adopting an existing fiscal receipt for {InvoiceNo} rather than signing it again "
            + "(receipt {ReceiptGlobalNo}, source {Source}).",
            invoiceNo,
            match?.ReceiptGlobalNo,
            response.Source);

        var message = $"Receipt {match?.ReceiptGlobalNo} was already signed for this sale; adopted it.";

        // The archived record carries the device signature, so the adopted receipt gets the same QR and
        // verification code a fresh submission would have: they are what the customer's copy and SAP's
        // U_Fiscal_Url are printed from. A record without a match still adopts — better than a second
        // receipt — with only what the check said.
        return match is null
            ? new FiscalizationResult { Success = true, Message = message, InvoiceNumber = invoiceNo }
            : await DescribeArchivedAsync(match, message, cancellationToken);
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
    /// <summary>
    /// The invoice number to fiscalise a SAP document under, or null to let the platform use its DocNum.
    /// </summary>
    /// <remarks>
    /// Only once the mobile invoice-number UDF is configured, and only for a document carrying a sale
    /// reference. Both write paths put the same string into that UDF and into <c>U_Van_saleorder</c>,
    /// which is what <see cref="InvoiceDto.VanSaleOrderNumber"/> reads back — so this is the reference
    /// the sale was stamped under on the handset, and using it makes the two fiscalisation routes agree
    /// on one identity instead of keying the same sale twice under two different numbers.
    ///
    /// Null while the UDF is unconfigured, which leaves today's behaviour exactly as it was: the platform
    /// derives the key from the SAP DocNum.
    /// </remarks>
    private string? ResolveMobileInvoiceNo(InvoiceDto document)
    {
        if (!_settings.Udf.WritesInvoiceNumber)
        {
            return null;
        }

        var reference = document.VanSaleOrderNumber?.Trim();
        return string.IsNullOrWhiteSpace(reference) ? null : reference;
    }

    internal string BuildPreSapInvoiceNo(string externalReference)
        => _settings.BuildPreSapInvoiceNo(externalReference);

    /// <summary>
    /// The lines <see cref="FiscalizePreSapInvoiceAsync"/> files for this invoice, without filing them.
    /// </summary>
    /// <remarks>
    /// The platform's receipt lookup returns a receipt's header and not its lines, so a till credit
    /// rebuilds them through this same mapping. See
    /// <see cref="Features.DesktopCreditNotes.PlatformDesktopCreditGateway"/>, which also asks for
    /// <see cref="PreSapLinePricing.Cents"/> to rebuild a receipt filed before exact pricing.
    /// </remarks>
    internal async Task<List<LineApiRequest>> BuildPreSapLinesAsync(
        InvoiceDto invoice,
        CancellationToken cancellationToken,
        PreSapLinePricing pricing = PreSapLinePricing.Exact)
    {
        var hsCodes = _itemHsCodes is null || invoice.Lines is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : await _itemHsCodes.ResolveAsync(invoice.Lines.Select(line => line.ItemCode), cancellationToken);

        var lines = MapLines(invoice, ReceiptType.FiscalInvoice, hsCodes, pricing);
        if (pricing == PreSapLinePricing.Exact)
        {
            ReconcileToDocumentTotal(lines, invoice.DocTotal);
        }

        return lines;
    }

    /// <summary>
    /// Moves the cent or two between the lines and the sale's total onto the largest line, so the
    /// receipt comes to exactly what the customer was charged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The platform totals a receipt as the sum of round(quantity x price, 2) over its lines. A sale is
    /// totalled as its net line totals plus VAT rounded once per rate, so even at exact prices the two
    /// can differ by a cent per line. The console's own SAP mapper settles the same difference the same
    /// way, against the SAP document's total.
    /// </para>
    /// <para>
    /// Only a rounding-sized difference is moved. A larger one means the caller's total is not the
    /// lines' gross total at all — a net figure, say — and folding it into one line would misprice that
    /// line, so the lines are filed as they are.
    /// </para>
    /// </remarks>
    private void ReconcileToDocumentTotal(List<LineApiRequest> lines, decimal documentTotal)
    {
        if (lines.Count == 0 || documentTotal <= 0m)
        {
            return;
        }

        var target = RoundCurrency(documentTotal);
        var difference = target - LinesTotal(lines);
        if (difference == 0m)
        {
            return;
        }

        if (Math.Abs(difference) > RoundingAllowancePerLine * lines.Count)
        {
            _logger.LogWarning(
                "Receipt lines come to {LinesTotal} against a document total of {DocumentTotal}, too far apart "
                + "to be rounding, so the lines are filed as they are.",
                LinesTotal(lines),
                target);
            return;
        }

        var line = lines
            .Where(l => l.Price > 0m && l.Quantity > 0m)
            .OrderByDescending(l => RoundCurrency(l.Price * l.Quantity))
            .FirstOrDefault();
        if (line is null)
        {
            return;
        }

        // Twice: at a large quantity the price's sixth decimal is itself worth more than a cent, so
        // the first pass can land a cent off. The second settles it from where the first landed.
        for (var pass = 0; pass < 2 && difference != 0m; pass++)
        {
            var lineTotal = RoundCurrency(line.Price * line.Quantity) + difference;
            if (lineTotal <= 0m)
            {
                return;
            }

            line.Price = RoundPrice(lineTotal / line.Quantity);
            difference = target - LinesTotal(lines);
        }
    }

    /// <summary>
    /// The most a sale's total and its exactly priced lines can differ by, per line, through rounding
    /// alone: the net line total's half cent grossed up, the gross line total's half cent, and the
    /// half cent of VAT rounding its rate shares, with room to spare.
    /// </summary>
    private const decimal RoundingAllowancePerLine = 0.02m;

    private static decimal LinesTotal(IEnumerable<LineApiRequest> lines)
        => lines.Sum(l => RoundCurrency(l.Price * l.Quantity));

    /// <summary>
    /// Files a till credit note built in full by the caller.
    /// </summary>
    /// <remarks>
    /// Returns a result only when the platform answered. A refusal it answered with, other than one it
    /// marks for reconciliation, is a failed result: nothing was filed under the number. Anything that
    /// leaves the outcome open — a 5xx other than <c>FdmsRequestNotSent</c>, a reply with no problem
    /// document, a lost connection — throws, so the caller records the credit as needing reconciliation
    /// rather than as refused. <see cref="Unexpected"/> would report those as an ordinary failure, which
    /// a credit's caller reads as "nothing was filed".
    /// </remarks>
    internal async Task<FiscalizationResult> SubmitDesktopCreditAsync(
        SubmitReceiptApiRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rawRequestJson = Serialize(request);

        try
        {
            var response = await _client.SubmitReceiptAsync(request, cancellationToken);

            _logger.LogInformation(
                "Fiscalised till credit {InvoiceNo}. ReceiptGlobalNo: {ReceiptGlobalNo}",
                response.InvoiceNo,
                response.ReceiptGlobalNo);

            return await MapSuccessAsync(response, rawRequestJson, cancellationToken);
        }
        catch (FiscalisationApiException ex) when (ex.HasProblemDocument
            && ((int)ex.StatusCode < 500
                || string.Equals(ex.ErrorCode, "FdmsRequestNotSent", StringComparison.OrdinalIgnoreCase)))
        {
            return await MapFailureAsync(
                ex, request.InvoiceNo ?? string.Empty, ReceiptType.CreditNote, rawRequestJson, cancellationToken);
        }
    }

    /// <summary>
    /// A receipt the platform has archived, with the QR code and verification code composed from it.
    /// </summary>
    internal async Task<FiscalizationResult> DescribeArchivedAsync(
        FiscalisedReceiptRecordDto match,
        string message,
        CancellationToken cancellationToken)
    {
        var config = await _configCache.TryGetAsync(match.DeviceId, cancellationToken);
        var verificationCode = FiscalReceiptQrComposer.TryCreateVerificationCode(match.DeviceSignatureValue);

        return new FiscalizationResult
        {
            Success = true,
            Message = message,
            InvoiceNumber = match.InvoiceNo,
            QRCode = FiscalReceiptQrComposer.BuildQrPayload(
                config?.QrUrl, match.DeviceId, match.ReceiptDate, match.ReceiptGlobalNo, verificationCode),
            VerificationCode = verificationCode is null
                ? null
                : FiscalReceiptQrComposer.FormatVerificationCode(verificationCode),
            FiscalDayNo = match.FiscalDayNo.ToString(CultureInfo.InvariantCulture),
            ReceiptGlobalNo = match.ReceiptGlobalNo.ToString(CultureInfo.InvariantCulture),
            ReceiptCounter = match.ReceiptCounter.ToString(CultureInfo.InvariantCulture),
            DeviceSerial = config?.DeviceSerialNo
        };
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
                var recovered = await DescribeArchivedAsync(
                    match,
                    $"Already fiscalised as receipt {match.ReceiptGlobalNo} on device {match.DeviceId}.",
                    cancellationToken);

                recovered.Skipped = true;
                recovered.RawRequestJson = rawRequestJson;
                recovered.RawResponseJson = Serialize(check);
                return recovered;
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
    ///
    /// Every line declares its tax percentage alongside its tax id. The platform matches the pair
    /// against the device's taxes and reads an absent percentage as exempt, so a standard-rated id with
    /// no percentage is refused with RCPT025 — every till sale was, from the cut-over on 30 September
    /// 2026. The percentage is the rate the line was charged at (<c>Tax:RatesByTaxCode</c>), not one
    /// looked up from the id, so a mapping that pairs a code with the wrong id is refused rather than
    /// filed under a rate the customer was not charged.
    ///
    /// The code is <see cref="InvoiceLineDto.TaxCode"/>, or <see cref="InvoiceLineDto.VatGroup"/> on a
    /// line read back from SAP, where TaxCode is null and the VAT group is where the code lives.
    ///
    /// The HS code is the item's own, from <c>OITM.FrgnName</c> (see <see cref="ItemHsCodes"/>), the
    /// same field the platform reads for a document it fiscalises out of SAP. Only an item SAP gives
    /// no usable code falls back to <see cref="FiscalisationSettings.DefaultHsCode"/>. Every till line
    /// used to take the default, so every item was declared to ZIMRA as 04031000.
    ///
    /// The price is the exact VAT-inclusive unit price (<see cref="InvoiceLineDto.PriceAfterVat"/>),
    /// to six decimals as the console's SAP mapper files it. It used to be rounded to the cent first,
    /// and the platform multiplies the unit price back out, so every receipt was filed at quantity x
    /// the rounded price rather than at what the till charged and SAP invoiced: KEF-FAC-20261005-
    /// 1B58CD07FF27 was charged 276.93 and filed with ZIMRA as 277.60. FDMS takes the longer price
    /// (receipt 11512485 was filed at 17.54445).
    /// </remarks>
    private List<LineApiRequest> MapLines(
        InvoiceDto invoice,
        ReceiptType receiptType,
        IReadOnlyDictionary<string, string> hsCodesByItem,
        PreSapLinePricing pricing)
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
                var price = pricing == PreSapLinePricing.Exact
                    ? RoundPrice(GetExactPriceAfterVat(line))
                    : RoundCurrency(GetPriceAfterVat(line));
                var taxCode = string.IsNullOrWhiteSpace(line.TaxCode) ? line.VatGroup : line.TaxCode;
                var itemCode = line.ItemCode?.Trim();
                var hsCode = !string.IsNullOrEmpty(itemCode) && hsCodesByItem.TryGetValue(itemCode, out var itemHsCode)
                    ? itemHsCode
                    : _settings.DefaultHsCode;

                return new LineApiRequest
                {
                    // Required, and capped at 200 characters by the platform. ItemDescription is
                    // nullable, so fall back to the code rather than sending an empty name.
                    Name = Truncate(
                        string.IsNullOrWhiteSpace(line.ItemDescription) ? line.ItemCode : line.ItemDescription,
                        200),
                    HsCode = hsCode,
                    Quantity = quantity <= 0m ? 1m : quantity,
                    Price = sign * price,
                    TaxId = ResolveTaxId(taxCode),
                    TaxPercent = ToFdmsTaxPercent(_tax.RateFor(taxCode))
                };
            })
            .ToList();
    }

    /// <summary>A rate as FDMS takes it: a percentage with at most two decimal places.</summary>
    /// <remarks>
    /// FDMS types the field decimal(5,2) and refuses anything with a longer scale, even when the extra
    /// digits are zeros. <c>0.155m * 100m</c> is <c>15.500m</c> — a decimal keeps the scale of its
    /// operands — and the platform forwards it as written, so FDMS answered "provided value TaxPercent do
    /// not satisfy decimal(5,2)" to the first till sale after the tax percent was added.
    /// </remarks>
    internal static decimal ToFdmsTaxPercent(decimal rate)
        => Math.Round(rate * 100m, 2, MidpointRounding.AwayFromZero);

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

    /// <summary>The unit price the customer paid, unrounded; the cent-rounded one for a caller that set no other.</summary>
    private static decimal GetExactPriceAfterVat(InvoiceLineDto line)
    {
        var priceAfterVat = Math.Abs(line.PriceAfterVat);
        return priceAfterVat > 0m ? priceAfterVat : GetPriceAfterVat(line);
    }

    /// <summary>A unit price to the scale the console's SAP mapper files.</summary>
    private static decimal RoundPrice(decimal value)
        => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static DateTime? ParseDocDate(string? docDate)
        => DateTime.TryParse(docDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static decimal RoundCurrency(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
}
