using ErrorOr;
using MediatR;

namespace ShopInventory.Features.FiscalReceipts.Commands.PickUpFailedFiscalReceipts;

/// <summary>
/// One scheduled pass that reads back the receipts of SAP invoices and credit notes whose fiscalisation failed.
/// </summary>
public sealed record PickUpFailedFiscalReceiptsCommand : IRequest<ErrorOr<PickUpFailedFiscalReceiptsResult>>;

/// <summary>A document the pass will ask the fiscal device about.</summary>
/// <param name="Document">Which kind of document it is.</param>
/// <param name="DocNum">The SAP document number, which the receipt is filed under.</param>
/// <param name="FirstRecordedAtUtc">
/// Its earliest row in the log, which is no later than its first attempt. A receipt dated well before it belongs to
/// an earlier document SAP gave the same number.
/// </param>
/// <param name="LastFailedAtUtc">When its latest attempt failed; the back-off counts from here.</param>
/// <param name="LatestFailure">Its latest failed row: what the receipt row is recorded against.</param>
public sealed record FiscalReceiptPickupCandidate(
    FiscalReceiptPickupDocument Document,
    int DocNum,
    DateTime FirstRecordedAtUtc,
    DateTime LastFailedAtUtc,
    FiscalReceiptPickupRow LatestFailure);

/// <summary>The parts of a failed row the recorded receipt repeats, so recording it needs no SAP read.</summary>
public sealed record FiscalReceiptPickupRow(
    string? CardCode,
    string? CardName,
    decimal DocTotal,
    decimal VatSum,
    string? Currency,
    string? OriginalInvoiceNumber,
    string SourceSystem);

public sealed class PickUpFailedFiscalReceiptsResult
{
    /// <summary>The device held a receipt, and it is now recorded against the document.</summary>
    public int PickedUp { get; set; }

    /// <summary>The device holds no receipt for the document yet; it is asked again after its back-off.</summary>
    public int NotYet { get; set; }

    /// <summary>
    /// The device holds a receipt under the number, dated before the document existed: an earlier document's, left
    /// alone. Asked again after the back-off, in case the device files this one too.
    /// </summary>
    public int OlderReceipt { get; set; }

    /// <summary>The device held a receipt but it could not be recorded here.</summary>
    public int NotRecorded { get; set; }

    /// <summary>Why the pass left the rest of its documents for the next one, or null when it went through them all.</summary>
    public string? StoppedBecause { get; set; }

    public int Total => PickedUp + NotYet + OlderReceipt + NotRecorded;
}
