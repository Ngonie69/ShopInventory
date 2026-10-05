using System.Globalization;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Services;

/// <summary>
/// Fiscalises through the platform, and still asks REVMax about anything it may already have filed.
/// </summary>
/// <remarks>
/// <para>
/// This is what <see cref="FiscalisationProvider.Platform"/> resolves to while <c>Revmax:Enabled</c> stays
/// on. It files every new document on the platform. Before it does, it checks REVMax for the documents
/// REVMax might already hold. The platform knows nothing about those receipts. Its duplicate check covers
/// only its own archive, so without this step an old invoice would read "Not Fiscalised", offer a
/// Fiscalise button, and file a second receipt when pressed. The same goes for a till sale left pending
/// at the switch-over.
/// </para>
/// <para>
/// A credit note goes to wherever its original lives. Only REVMax can credit a REVMax receipt. The
/// platform refuses an original it has not archived (RCPT032), and ZIMRA links a credit note to its
/// original through the device chain the original sits on.
/// </para>
/// <para>
/// REVMax is asked only where the answer could be yes. It is a device on its way out, and a lookup
/// that cannot reach it has to stop the filing rather than be read as "not there". Asking it about
/// every new invoice would turn a REVMax outage into a fiscalisation outage.
/// <see cref="RevmaxSettings.LastFilingDate"/> is what makes that possible.
/// </para>
/// <para>
/// Retire REVMax by setting <c>Revmax:Enabled</c> to false. Do that once nothing it filed is left to
/// credit and nothing from before the switch is still pending. The plain platform service then takes
/// over and REVMax is never asked again.
/// </para>
/// </remarks>
public sealed class RevmaxHistoryFiscalizationService(
    FiscalizationService platform,
    RevmaxFiscalizationService revmax,
    IOptions<RevmaxSettings> revmaxOptions,
    ILogger<RevmaxHistoryFiscalizationService> logger) : IFiscalizationService
{
    /// <summary>The error code for a filing held back because REVMax could not be asked first.</summary>
    public const string HistoryUnavailableErrorCode = "REVMAX_HISTORY_UNAVAILABLE";

    public async Task<FiscalizationResult> FiscalizeInvoiceAsync(
        InvoiceDto invoice,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var invoiceNumber = invoice.DocNum.ToString(CultureInfo.InvariantCulture);

        if (MayBeOnRevmax(invoice.DocDate))
        {
            var held = await revmax.LookUpOwnReceiptAsync(
                invoiceNumber, RevmaxFiscalizationService.InvoiceReceiptType, cancellationToken);

            if (Settled(held, invoiceNumber) is { } settled)
            {
                return settled;
            }
        }

        return await platform.FiscalizeInvoiceAsync(invoice, customerDetails, cancellationToken);
    }

    /// <remarks>
    /// Straight to the platform. A pre-SAP sale's first attempt is always new. Every later attempt comes
    /// through <see cref="FindPreSapReceiptAsync"/> first, and that is where REVMax is asked.
    /// </remarks>
    public Task<FiscalizationResult> FiscalizePreSapInvoiceAsync(
        InvoiceDto invoice,
        string externalReference,
        CustomerFiscalDetails? customerDetails = null,
        MoneyType? paymentType = null,
        ReceiptPrintForm printForm = ReceiptPrintForm.InvoiceA4,
        FiscalReceiptSource? source = null,
        CancellationToken cancellationToken = default)
        => platform.FiscalizePreSapInvoiceAsync(
            invoice, externalReference, customerDetails, paymentType, printForm, source, cancellationToken);

    public async Task<FiscalizationResult> FiscalizeCreditNoteAsync(
        InvoiceDto creditNote,
        string originalInvoiceNumber,
        CustomerFiscalDetails? customerDetails = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creditNote);

        var creditNoteNumber = creditNote.DocNum.ToString(CultureInfo.InvariantCulture);

        if (MayBeOnRevmax(creditNote.DocDate))
        {
            var held = await revmax.LookUpOwnReceiptAsync(
                creditNoteNumber, RevmaxFiscalizationService.CreditNoteReceiptType, cancellationToken);

            if (Settled(held, creditNoteNumber) is { } settled)
            {
                return settled;
            }
        }

        // No original named: the platform recovers the link from SAP's BaseEntry, and an original REVMax
        // filed cannot be recovered that way by anything here either.
        if (string.IsNullOrWhiteSpace(originalInvoiceNumber))
        {
            return await platform.FiscalizeCreditNoteAsync(
                creditNote, originalInvoiceNumber, customerDetails, cancellationToken);
        }

        // The platform is asked first because it holds everything filed since the switch. That way a
        // credit against a new invoice never depends on REVMax answering.
        if (await platform.IsInvoiceFiscalizedAsync(originalInvoiceNumber, cancellationToken))
        {
            return await platform.FiscalizeCreditNoteAsync(
                creditNote, originalInvoiceNumber, customerDetails, cancellationToken);
        }

        var original = await revmax.LookUpOwnReceiptAsync(
            originalInvoiceNumber, RevmaxFiscalizationService.InvoiceReceiptType, cancellationToken);

        switch (original.Outcome)
        {
            case RevmaxReceiptLookupOutcome.Found:
                logger.LogInformation(
                    "Crediting {CreditNoteNumber} on REVMax: its original invoice {OriginalInvoiceNumber} "
                    + "was filed there, and only that device can reverse it.",
                    creditNoteNumber,
                    originalInvoiceNumber);

                return await revmax.FiscalizeCreditNoteAsync(
                    creditNote, originalInvoiceNumber, customerDetails, cancellationToken);

            case RevmaxReceiptLookupOutcome.Unknown:
                return HeldBack(
                    creditNoteNumber,
                    $"Not fiscalising credit note {creditNoteNumber} yet. Its original invoice "
                    + $"{originalInvoiceNumber} is not on the platform, and REVMax could not be asked "
                    + $"whether it filed it. That answer decides which device can credit the invoice. "
                    + $"{original.Reason}");

            default:
                // Neither holds it. The platform gives the refusal, which names the reason in its own
                // terms, rather than this class guessing at it.
                return await platform.FiscalizeCreditNoteAsync(
                    creditNote, originalInvoiceNumber, customerDetails, cancellationToken);
        }
    }

    public async Task<bool> IsInvoiceFiscalizedAsync(
        string invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        if (await platform.IsInvoiceFiscalizedAsync(invoiceNumber, cancellationToken))
        {
            return true;
        }

        var held = await revmax.LookUpOwnReceiptAsync(
            invoiceNumber, RevmaxFiscalizationService.InvoiceReceiptType, cancellationToken);

        return held.Outcome == RevmaxReceiptLookupOutcome.Found;
    }

    /// <remarks>
    /// Null only when both devices say plainly they hold nothing. If either cannot be asked, this throws,
    /// as each does alone: the sale is left as it is for the next pass, not signed a second time. The
    /// REVMax half uses the same prefixed number REVMax filed pre-SAP sales under, so a till sale left
    /// pending across the switch-over is found where it was actually signed.
    /// </remarks>
    public async Task<FiscalizationResult?> FindPreSapReceiptAsync(
        string externalReference,
        CancellationToken cancellationToken = default)
        => await platform.FindPreSapReceiptAsync(externalReference, cancellationToken)
           ?? await revmax.FindPreSapReceiptAsync(externalReference, cancellationToken);

    /// <summary>
    /// Whether a document of this date could have been filed on REVMax.
    /// </summary>
    /// <remarks>
    /// Yes when no last filing date is configured, and when the date cannot be read. Asking REVMax when
    /// it was not needed costs one LAN call. Skipping it when it was needed costs a duplicate receipt.
    /// </remarks>
    private bool MayBeOnRevmax(string? documentDate)
    {
        var lastFilingDate = revmaxOptions.Value.LastFilingDate;

        if (lastFilingDate is null)
        {
            return true;
        }

        return !DateTime.TryParse(
                   documentDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
               || parsed.Date <= lastFilingDate.Value.Date;
    }

    /// <summary>
    /// The answer to return without filing, or null when REVMax holds nothing and the platform may file.
    /// </summary>
    private FiscalizationResult? Settled(RevmaxReceiptLookup held, string documentNumber) => held.Outcome switch
    {
        RevmaxReceiptLookupOutcome.Found => held.Receipt,
        RevmaxReceiptLookupOutcome.Unknown => HeldBack(
            documentNumber,
            $"Not fiscalising {documentNumber} yet. It is dated on or before REVMax's last filing day, and "
            + $"REVMax could not be asked whether it already filed it. Filing it on the platform might "
            + $"sign a second receipt, and a fiscal receipt cannot be withdrawn. {held.Reason}"),
        _ => null
    };

    private FiscalizationResult HeldBack(string documentNumber, string message)
    {
        logger.LogWarning("{Message}", message);

        return new FiscalizationResult
        {
            Success = false,
            InvoiceNumber = documentNumber,
            ErrorCode = HistoryUnavailableErrorCode,
            Message = message
        };
    }
}
