using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Posts the day's held van sales to SAP, one A/R invoice per sale.
///
/// One-to-one, not consolidated, and that is the whole design. Each of these sales was stamped with its
/// own ZIMRA receipt on the handset hours earlier; folding several into one SAP invoice would leave every
/// SAP document mapping to several receipts and no receipt mapping to a document, which is precisely the
/// join the SAP↔FDMS reconciliation report needs. It also removes any chance of the consolidated invoice
/// being fiscalised a second time.
///
/// Posting is deferred to end of day rather than done on upload because a van trades out of coverage:
/// its sales arrive in bursts whenever it finds signal, and SAP should see a settled day rather than a
/// trickle that stops mid-afternoon when the van drives out of range.
/// </summary>
public sealed class VanSalesEndOfDayPostingService(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    ILogger<VanSalesEndOfDayPostingService> logger)
{
    /// <summary>
    /// After this many failed attempts a sale stops being retried automatically and waits for a human.
    /// Two runs a night means a sale that is genuinely wrong stops re-attempting after a few days rather
    /// than raising the same alarm forever.
    /// </summary>
    private const int MaxPostingAttempts = 6;

    public async Task<VanSalesPostingRunResult> PostPendingSalesAsync(
        DateTime tradingDate,
        CancellationToken cancellationToken = default)
    {
        var date = tradingDate.Date;

        var pending = await context.DesktopSales
            .Include(s => s.Lines)
            .Where(s => s.DocDate == date &&
                        s.SourceSystem == SaleSourceSystems.VanSales &&
                        s.ConsolidationStatus == DesktopSaleConsolidationStatus.Pending &&
                        s.PostingAttempts < MaxPostingAttempts)
            .OrderBy(s => s.ReceiptGlobalNo)
            .ToListAsync(cancellationToken);

        var result = new VanSalesPostingRunResult(date);

        if (pending.Count == 0)
        {
            // Not an error. The mop-up run finds nothing on most nights, and treating that as a failure
            // would raise an alarm nightly and train everyone to ignore the one that matters.
            logger.LogInformation("No van sales are awaiting posting for {TradingDate:yyyy-MM-dd}.", date);
            return result;
        }

        logger.LogInformation(
            "Posting {Count} van sales for {TradingDate:yyyy-MM-dd} to SAP.", pending.Count, date);

        foreach (var sale in pending)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Stop cleanly rather than half-posting the tail of the batch. Whatever is left stays
                // Pending and the mop-up picks it up.
                logger.LogWarning(
                    "Van sales posting for {TradingDate:yyyy-MM-dd} was cancelled after {Posted} of {Total}.",
                    date, result.Posted, pending.Count);
                break;
            }

            try
            {
                await PostOneAsync(sale, result, cancellationToken);
            }
            catch (Exception ex)
            {
                // Never let one sale stop the run: the rest of the day's takings still need to reach SAP,
                // and this one will be offered again by the mop-up.
                sale.PostingAttempts++;
                sale.LastPostingError = Truncate(ex.Message, 2000);
                result.Failed++;
                result.Errors.Add($"{sale.ExternalReferenceId}: {ex.Message}");

                logger.LogError(
                    ex,
                    "Failed to post van sale {ExternalReference} (receipt {ReceiptGlobalNo}) to SAP. Attempt {Attempt} of {Max}.",
                    sale.ExternalReferenceId,
                    sale.ReceiptGlobalNo,
                    sale.PostingAttempts,
                    MaxPostingAttempts);
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Van sales posting for {TradingDate:yyyy-MM-dd} finished: {Posted} posted, {Adopted} already in SAP, {Failed} failed.",
            date, result.Posted, result.Adopted, result.Failed);

        return result;
    }

    private async Task PostOneAsync(
        DesktopSaleEntity sale,
        VanSalesPostingRunResult result,
        CancellationToken cancellationToken)
    {
        // The handset's van_order is the business key all the way through, and SAP holds it in
        // U_Van_saleorder. Asking SAP first is what makes the 19:30 mop-up safe to run over sales the
        // 18:00 run may have posted just before losing its connection: an invoice that already exists is
        // adopted rather than posted again.
        var existing = await sapClient.GetInvoiceByVanSaleOrderAsync(sale.ExternalReferenceId, cancellationToken);
        if (existing is not null)
        {
            MarkPosted(sale, existing.DocEntry, existing.DocNum);
            result.Adopted++;

            logger.LogInformation(
                "Van sale {ExternalReference} was already in SAP as invoice {DocNum}; adopted it rather than posting again.",
                sale.ExternalReferenceId,
                existing.DocNum);
            return;
        }

        var request = BuildInvoiceRequest(sale);
        var invoice = await sapClient.CreateInvoiceAsync(request, cancellationToken);

        MarkPosted(sale, invoice.DocEntry, invoice.DocNum);
        result.Posted++;

        logger.LogInformation(
            "Posted van sale {ExternalReference} (ZIMRA receipt {ReceiptGlobalNo}) to SAP as invoice {DocNum}.",
            sale.ExternalReferenceId,
            sale.ReceiptGlobalNo,
            invoice.DocNum);
    }

    private static CreateInvoiceRequest BuildInvoiceRequest(DesktopSaleEntity sale) => new()
    {
        CardCode = sale.CardCode,
        DocDate = sale.DocDate.ToString("yyyy-MM-dd"),
        DocDueDate = sale.DocDate.ToString("yyyy-MM-dd"),
        NumAtCard = sale.ExternalReferenceId,
        // Both the SAP-side duplicate guard and the local idempotency key. Set from the handset's own
        // reference so a retry, a mop-up and a manual re-run all collapse onto the same document.
        U_Van_saleorder = sale.ExternalReferenceId,
        ClientRequestId = sale.ExternalReferenceId,
        DocCurrency = sale.Currency,
        Comments = BuildComments(sale),
        Lines = sale.Lines
            .OrderBy(l => l.LineNum)
            .Select(l => new CreateInvoiceLineRequest
            {
                ItemCode = l.ItemCode,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                WarehouseCode = string.IsNullOrWhiteSpace(l.WarehouseCode) ? sale.WarehouseCode : l.WarehouseCode,
                TaxCode = l.TaxCode,
                DiscountPercent = l.DiscountPercent,
                UoMCode = l.UoMCode,
                CostCentreCode = sale.CostCentreCode,
                // FEFO server-side, matching the online van sales path. The handset does not choose
                // batches: it sells from a van whose stock it tracks by item, not by batch.
                AutoAllocateBatches = true,
                BatchNumbers = null
            })
            .ToList()
    };

    /// <summary>
    /// Carries the ZIMRA receipt onto the SAP document so the link is visible to anyone reading the
    /// invoice in SAP, not only to a reconciliation query that knows to join on it.
    /// </summary>
    private static string BuildComments(DesktopSaleEntity sale)
    {
        var parts = new List<string> { "Van sale, fiscalised on device" };

        if (!string.IsNullOrWhiteSpace(sale.FiscalDeviceNumber))
        {
            parts.Add($"device {sale.FiscalDeviceNumber}");
        }

        if (!string.IsNullOrWhiteSpace(sale.FiscalDayNo))
        {
            parts.Add($"fiscal day {sale.FiscalDayNo}");
        }

        if (sale.ReceiptGlobalNo.HasValue)
        {
            parts.Add($"receipt {sale.ReceiptGlobalNo}");
        }

        return Truncate(string.Join(", ", parts) + ".", 500);
    }

    private static void MarkPosted(DesktopSaleEntity sale, int docEntry, int docNum)
    {
        sale.SapDocEntry = docEntry;
        sale.SapDocNum = docNum;
        sale.PostedAt = DateTime.UtcNow;
        sale.PostingAttempts++;
        sale.LastPostingError = null;
        sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
    }

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// What one posting run did. <see cref="Adopted"/> is kept separate from <see cref="Posted"/> because
/// they mean different things operationally: adopting means an earlier run reached SAP but did not
/// record it locally, which is worth noticing if it happens often.
/// </summary>
public sealed class VanSalesPostingRunResult(DateTime tradingDate)
{
    public DateTime TradingDate { get; } = tradingDate;
    public int Posted { get; set; }
    public int Adopted { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = [];

    public int Total => Posted + Adopted + Failed;
}
