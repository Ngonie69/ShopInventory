using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
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
///
/// A run covers the requested day and the <see cref="VanSalesPostingSettings.LookbackDays"/> before it,
/// because the same coverage gap that defers posting also defers upload: a sale carries the trading day
/// it was sold on, so one uploaded the next morning arrives already dated to a day no future run would
/// otherwise ask for.
/// </summary>
public sealed class VanSalesEndOfDayPostingService(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IOptions<VanSalesPostingSettings> settings,
    ILogger<VanSalesEndOfDayPostingService> logger)
{
    /// <summary>
    /// After this many rejections a sale stops being retried automatically and waits for a human.
    /// </summary>
    /// <remarks>
    /// Only a rejection spends one, and that qualifier is load-bearing now in a way it was not when this
    /// was written. Six attempts across two runs a night meant a sale that was genuinely wrong stopped
    /// re-attempting after a few days; the half-hourly pass added later spends the same six inside three
    /// hours. Counting a SAP outage against the budget would therefore park a whole day's van takings
    /// behind a human over a blip that healed itself before lunch.
    ///
    /// What the cap still means is the thing it always meant: six attempts at a sale SAP actually
    /// refuses — a blocked item, a closed period — which is not going to fix itself, and is better in
    /// front of somebody within hours than after three days.
    /// </remarks>
    internal const int MaxPostingAttempts = 6;

    public async Task<VanSalesPostingRunResult> PostPendingSalesAsync(
        DateTime tradingDate,
        CancellationToken cancellationToken = default)
    {
        var date = tradingDate.Date;

        // A window ending on the requested day, not the day alone. DocDate is the handset's trading
        // day: a sale made at 21:00 by a van that only found signal the next morning is stored against
        // yesterday, and every trigger asks for today. Matched exactly, that sale would be offered to
        // SAP once — before it had been uploaded — and never again, and it would sit Pending with no
        // attempts recorded and nothing to surface it. The till route learned this first; see
        // DesktopSalePostingSettings.LookbackDays.
        //
        // The upper bound is safe in a way the lower one is not: a handset whose clock runs ahead posts
        // when its date arrives, a day or two late, while a sale behind the run date has no later run
        // coming for it at all.
        var windowStart = settings.Value.WindowStart(date);

        var pending = await context.DesktopSales
            .Include(s => s.Lines)
            .Where(s => s.DocDate >= windowStart &&
                        s.DocDate <= date &&
                        s.SourceSystem == SaleSourceSystems.VanSales &&
                        s.ConsolidationStatus == DesktopSaleConsolidationStatus.Pending &&
                        s.PostingAttempts < MaxPostingAttempts)
            // Oldest day first, so a backlog reaches SAP in the order it was sold rather than the order
            // it happened to be uploaded in.
            .OrderBy(s => s.DocDate)
            .ThenBy(s => s.ReceiptGlobalNo)
            .ToListAsync(cancellationToken);

        var result = new VanSalesPostingRunResult(date, windowStart);

        if (pending.Count == 0)
        {
            // Not an error. The mop-up run finds nothing on most nights, and treating that as a failure
            // would raise an alarm nightly and train everyone to ignore the one that matters.
            logger.LogInformation(
                "No van sales are awaiting posting for {WindowStart:yyyy-MM-dd} to {TradingDate:yyyy-MM-dd}.",
                windowStart, date);
            return result;
        }

        logger.LogInformation(
            "Posting {Count} van sales for {WindowStart:yyyy-MM-dd} to {TradingDate:yyyy-MM-dd} to SAP.",
            pending.Count, windowStart, date);

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

            // Never let one sale stop the run: the rest of the day's takings still need to reach SAP,
            // and this one will be offered again by the mop-up.
            await TryPostAsync(sale, result, cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Van sales posting for {WindowStart:yyyy-MM-dd} to {TradingDate:yyyy-MM-dd} finished: {Posted} posted, {Adopted} already in SAP, {Failed} failed.",
            windowStart, date, result.Posted, result.Adopted, result.Failed);

        return result;
    }

    /// <summary>
    /// Posts one named sale, whatever day it belongs to and whatever its attempt count. Returns null
    /// when there is no such sale, or when it is not this route's to post.
    /// </summary>
    /// <remarks>
    /// What the exception center's retry calls. Resetting the sale's attempt count and waiting for the
    /// next pass would not do: the sales most in need of a retry are the ones that fell out of the
    /// lookback window, and no pass is ever going to ask for their day again.
    ///
    /// The attempt cap is not applied here either. It exists to stop an automatic pass re-attempting a
    /// hopeless sale twice an hour forever, and a person pressing retry has said otherwise.
    /// </remarks>
    public async Task<VanSalesPostingRunResult?> PostSaleAsync(
        int saleId,
        CancellationToken cancellationToken = default)
    {
        var sale = await context.DesktopSales
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken);

        if (sale is null ||
            sale.SourceSystem != SaleSourceSystems.VanSales ||
            sale.ConsolidationStatus != DesktopSaleConsolidationStatus.Pending)
        {
            return null;
        }

        // Its own trading day at both ends: this posts exactly one sale, so there is no window.
        var result = new VanSalesPostingRunResult(sale.DocDate.Date, sale.DocDate.Date);

        await TryPostAsync(sale, result, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Van sale {ExternalReference} was posted on request: {Posted} posted, {Adopted} already in SAP, {Failed} failed.",
            sale.ExternalReferenceId, result.Posted, result.Adopted, result.Failed);

        return result;
    }

    /// <summary>
    /// Posts one sale and records what happened to it, a failure included. Never throws: a sale SAP
    /// refuses is an outcome to be written down, not an exception every caller has to be ready for.
    /// </summary>
    private async Task TryPostAsync(
        DesktopSaleEntity sale,
        VanSalesPostingRunResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await PostOneAsync(sale, result, cancellationToken);
        }
        catch (Exception ex)
        {
            // SAP being unreachable is not the sale's fault, so it does not spend one of the sale's
            // attempts. See MaxPostingAttempts: at a pass every half hour, counting an outage would
            // exhaust every van sale of the day inside three hours and hand the lot to a human.
            var transient = SapFailureClassifier.IsTransient(ex, cancellationToken);
            if (!transient)
            {
                sale.PostingAttempts++;
            }

            sale.LastPostingError = Truncate(ex.Message, 2000);
            result.Failed++;
            result.Errors.Add($"{sale.ExternalReferenceId}: {ex.Message}");

            logger.LogError(
                ex,
                "Failed to post van sale {ExternalReference} (receipt {ReceiptGlobalNo}) to SAP. "
                + "Attempt {Attempt} of {Max}{Transient}.",
                sale.ExternalReferenceId,
                sale.ReceiptGlobalNo,
                sale.PostingAttempts,
                MaxPostingAttempts,
                // Otherwise a run of transient failures reads as the same attempt number repeating, which
                // looks like a stuck counter rather than the budget being deliberately left alone.
                transient ? " (SAP unreachable, so no attempt was spent)" : string.Empty);
        }
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

    private static CreateInvoiceRequest BuildInvoiceRequest(DesktopSaleEntity sale) =>
        DesktopSaleInvoiceRequestBuilder.Build(sale, BuildComments(sale));

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
public sealed class VanSalesPostingRunResult(DateTime tradingDate, DateTime windowStart)
{
    /// <summary>The day the run was asked for, and the last day of the window it covered.</summary>
    public DateTime TradingDate { get; } = tradingDate;

    /// <summary>
    /// The first day the run looked at. Recorded next to <see cref="TradingDate"/> so a log line
    /// explains why a run posted a sale from several days ago.
    /// </summary>
    public DateTime WindowStart { get; } = windowStart;
    public int Posted { get; set; }
    public int Adopted { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = [];

    public int Total => Posted + Adopted + Failed;
}
