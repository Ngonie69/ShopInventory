using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// Answers whether a SAP invoice is the record of a sale that was already fiscalised, one receipt at
/// a time, before it reached SAP.
/// </summary>
/// <remarks>
/// The sibling of <see cref="ConsolidatedInvoiceRegistry"/>, for the routes that post one invoice per
/// sale instead of one per customer per day: van sales, shop till sales and vending.
///
/// Without it those invoices are indistinguishable from an ordinary unfiscalised SAP invoice. Nothing
/// downstream can see the receipt, because the receipt was signed under the sale's own external
/// reference while every lookup here is keyed on the SAP DocNum — two different numbers, so even
/// FDMS's own duplicate guard, keyed on (TaxPayerTIN, ReceiptType, InvoiceNo), cannot catch it. The
/// invoice therefore reads "Unknown", the backfill records it as "Not Fiscalised", the Fiscalise
/// button appears, and one click submits a sale the customer already holds a receipt for to FDMS a
/// second time. That is irreversible: a duplicate fiscal receipt cannot be withdrawn.
///
/// Two markers, each written in the same SaveChanges that records its post, so neither can go missing
/// while the invoice exists — the same reason the consolidated registry reads its own table rather
/// than the fiscal transaction log, whose write is best-effort and happens after the SAP post has
/// already committed:
///
/// <list type="bullet">
/// <item>A sale row whose <c>SapDocNum</c> names the invoice — tills, vending, and van sales that
/// arrived as sales.</item>
/// <item>A reservation confirmed as the invoice, whose queue entry fiscalised it first — a van sale
/// that reached SAP through the invoice queue, such as a converted sales order. The reservation's
/// DocNum and status are saved together, and the queue entry was fiscalised before the post began.</item>
/// </list>
/// </remarks>
internal static class PerSaleInvoiceRegistry
{
    /// <summary>
    /// Returns the fiscalised sale that produced <paramref name="docNum"/>, or null if that invoice
    /// did not come from one.
    /// </summary>
    /// <remarks>
    /// Filtered on a successful fiscalisation, unlike the consolidated registry. There the receipts
    /// are with FDMS regardless of what the consolidation did afterwards; here the sale IS the
    /// receipt, so a sale that never fiscalised has nothing to protect and its invoice should stay
    /// fiscalisable by hand.
    /// </remarks>
    public static async Task<PerSaleInvoice?> FindByDocNumAsync(
        ApplicationDbContext dbContext,
        int docNum,
        CancellationToken cancellationToken)
    {
        if (docNum <= 0)
        {
            return null;
        }

        var sale = await dbContext.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SapDocNum == docNum
                && sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success)
            .OrderByDescending(sale => sale.Id)
            .Select(sale => new PerSaleInvoice(sale.ExternalReferenceId, sale.FiscalReceiptNumber))
            .FirstOrDefaultAsync(cancellationToken);

        if (sale is not null)
        {
            return sale;
        }

        return await FiscalisedQueuedSales(dbContext)
            .Where(queued => queued.DocNum == docNum)
            .OrderByDescending(queued => queued.QueueId)
            .Select(queued => new PerSaleInvoice(queued.ExternalReference, queued.FiscalReceiptNumber))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The receipt a per-sale invoice's own sale holds: the QR and the details printed beside it.
    /// </summary>
    /// <remarks>
    /// The fiscal detail on a SAP invoice is otherwise only ever found under its DocNum — the
    /// projector reads <c>DesktopFiscalTransactions</c> by DocNum, and the device read-back asks the
    /// device about the same number. For a per-sale invoice neither can answer, for the reason this
    /// whole class exists: the receipt was signed under the sale's own external reference, hours
    /// before SAP assigned a DocNum. So the invoice was printed with no QR, no verification code, no
    /// fiscal day and no device — the entire fiscal block dropped from a tax invoice that does hold a
    /// receipt, and dropped silently.
    ///
    /// The sale row carries all of it already, so this is a local read rather than another question
    /// to the device, and it cannot come and go with the device's availability.
    ///
    /// Only the sale-row marker answers here. A van sale that reached SAP through the invoice queue
    /// is matched by its reservation, and the queue entry holds a receipt number but not the QR, so
    /// such an invoice still falls through to the device read-back.
    /// </remarks>
    public static async Task<PerSaleFiscalReceipt?> FindReceiptByDocNumAsync(
        ApplicationDbContext dbContext,
        int docNum,
        CancellationToken cancellationToken)
    {
        if (docNum <= 0)
        {
            return null;
        }

        var receipt = await dbContext.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SapDocNum == docNum
                && sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success)
            .OrderByDescending(sale => sale.Id)
            .Select(sale => new PerSaleFiscalReceipt(
                sale.FiscalQRCode,
                sale.FiscalVerificationCode,
                sale.FiscalDayNo,
                sale.FiscalDeviceId,
                sale.ReceiptGlobalNo))
            .FirstOrDefaultAsync(cancellationToken);

        // A row with neither is nothing to print, and saying so lets the caller carry on to the
        // device rather than treat the invoice as answered.
        if (receipt is null
            || (string.IsNullOrWhiteSpace(receipt.QrCode)
                && string.IsNullOrWhiteSpace(receipt.VerificationCode)))
        {
            return null;
        }

        return receipt;
    }

    /// <summary>
    /// Narrows a page of document numbers to those that came from an already-fiscalised sale, in one
    /// query per marker.
    /// </summary>
    public static async Task<HashSet<int>> FindPerSaleDocNumsAsync(
        ApplicationDbContext dbContext,
        IEnumerable<int> docNums,
        CancellationToken cancellationToken)
    {
        var candidates = docNums
            .Where(docNum => docNum > 0)
            .Distinct()
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        var fromSales = await dbContext.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SapDocNum.HasValue
                && sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success
                && candidates.Contains(sale.SapDocNum.Value))
            .Select(sale => sale.SapDocNum!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var fromQueue = await FiscalisedQueuedSales(dbContext)
            .Where(queued => candidates.Contains(queued.DocNum))
            .Select(queued => queued.DocNum)
            .Distinct()
            .ToListAsync(cancellationToken);

        return [.. fromSales, .. fromQueue];
    }

    /// <summary>
    /// Every invoice confirmed from a reservation whose queue entry fiscalised the sale first.
    /// </summary>
    /// <remarks>
    /// Fiscalized or Completed, and requiring fiscalisation: <c>InvoicePostingJob</c> moves such an
    /// entry to Fiscalized only once the receipt is lodged, and only the queue's own posting moves it on
    /// to Completed. An entry that did not require fiscalisation proves nothing here — its receipt, if
    /// any, was signed on a handset and is marked by the sale row above.
    /// </remarks>
    private static IQueryable<FiscalisedQueuedSale> FiscalisedQueuedSales(ApplicationDbContext dbContext) =>
        from reservation in dbContext.StockReservations.AsNoTracking()
        join queued in dbContext.InvoiceQueue.AsNoTracking()
            on reservation.ReservationId equals queued.ReservationId
        where reservation.Status == ReservationStatus.Confirmed
            && reservation.SAPDocNum.HasValue
            && queued.RequiresFiscalization
            && (queued.Status == InvoiceQueueStatus.Fiscalized || queued.Status == InvoiceQueueStatus.Completed)
        select new FiscalisedQueuedSale
        {
            QueueId = queued.Id,
            DocNum = reservation.SAPDocNum!.Value,
            ExternalReference = queued.ExternalReference,
            FiscalReceiptNumber = queued.FiscalReceiptNumber
        };

    /// <remarks>
    /// Initialised rather than constructed, because the callers filter on it after the projection and
    /// EF can translate a member it saw assigned, but not one hidden behind a constructor parameter.
    /// </remarks>
    private sealed class FiscalisedQueuedSale
    {
        public int QueueId { get; init; }
        public int DocNum { get; init; }
        public string ExternalReference { get; init; } = string.Empty;
        public string? FiscalReceiptNumber { get; init; }
    }
}

/// <summary>
/// The sale a per-sale invoice records: the reference it was fiscalised under, and its receipt.
/// </summary>
internal sealed record PerSaleInvoice(string ExternalReferenceId, string? FiscalReceiptNumber);

/// <summary>
/// The ZIMRA receipt held against a sale, as the invoice PDF prints it.
/// </summary>
/// <remarks>
/// <paramref name="DeviceId"/> is the numeric ZIMRA device, which the document states as text, and
/// not <c>FiscalDeviceNumber</c> — that column carries a serial on one path and the device id on
/// another, so it cannot be printed as either.
/// </remarks>
internal sealed record PerSaleFiscalReceipt(
    string? QrCode,
    string? VerificationCode,
    string? FiscalDay,
    int? DeviceId,
    int? ReceiptGlobalNo);
