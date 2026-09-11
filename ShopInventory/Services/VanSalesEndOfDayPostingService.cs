using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
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
    SapCircuitBreakerState circuitState,
    IBatchInventoryValidationService batchValidation,
    IStockLedger stockLedger,
    IDesktopSalePostGuard postGuard,
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

        // Do not start a pass while SAP is known to be down. Nothing breaks without this — the HTTP
        // handler refuses each call before it leaves the process, and a circuit-open failure is
        // transient, so no sale spends an attempt over it. What it saves is the noise: this route posts
        // one sale at a time, so a pass during an outage is two doomed round trips and one logged error
        // per held sale, every one of them saying the same thing about SAP rather than about the sale.
        if (circuitState.ShouldShortCircuit(out var retryAfter))
        {
            logger.LogInformation(
                "Skipping van sales posting: the SAP circuit is open for another {RetryAfter}.", retryAfter);
            return new VanSalesPostingRunResult(date, windowStart);
        }

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

        // One reading of each van's warehouse for the whole run, rather than one per sale. A van
        // sells the same few lines all day, and each of these reads holds one of six process-wide
        // Service Layer slots for as long as SAP takes to answer.
        //
        // Safe to hold only because every sale that reaches SAP is taken off the reading. See
        // IStockReadWindow.
        using var readWindow = batchValidation.BeginSharedReadWindow();

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
            await TryPostAsync(sale, result, readWindow, cancellationToken);
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

        // Answered without a doomed round trip, and without touching the sale. Letting it through would
        // overwrite LastPostingError with the circuit message, losing the SAP rejection that is the
        // reason somebody is looking at this sale in the first place.
        if (circuitState.ShouldShortCircuit(out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            result.Failed++;
            result.Errors.Add(
                $"{sale.ExternalReferenceId}: SAP is unavailable; the circuit is open for another {seconds} seconds.");

            logger.LogInformation(
                "Refused to post van sale {ExternalReference} on request: the SAP circuit is open for another {RetryAfter}.",
                sale.ExternalReferenceId, retryAfter);
            return result;
        }

        await TryPostAsync(sale, result, NoStockReadWindow.Instance, cancellationToken);
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
        IStockReadWindow readWindow,
        CancellationToken cancellationToken)
    {
        try
        {
            await PostOneAsync(sale, result, readWindow, cancellationToken);
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
        IStockReadWindow readWindow,
        CancellationToken cancellationToken)
    {
        // One post per sale at a time. The lookups below make a *sequence* of attempts safe; they say
        // nothing about two running at once, and a van sale is the strictest case in the system — it
        // already carries its own ZIMRA receipt, so a second invoice is a second fiscal document for
        // one sale. The claim is what stops the half-hourly pass and a person pressing Post from each
        // finding no invoice and sending one.
        await using var claim = await postGuard.ClaimAsync(sale, cancellationToken);

        if (claim.Outcome == DesktopSalePostClaimOutcome.InFlight)
        {
            // The sale is left untouched: the other post owns its outcome, and writing here would
            // replace the SAP rejection somebody is reading with a note about scheduling.
            result.InFlight++;

            logger.LogInformation(
                "Left van sale {ExternalReference} alone: a post for it is already in flight.",
                sale.ExternalReferenceId);
            return;
        }

        if (claim.Receipt is { } receipt)
        {
            // Posted outside this run, so whether the shared reading was taken before or after that
            // invoice is not knowable from here. Thrown away rather than guessed at.
            readWindow.Discard();

            MarkPosted(sale, receipt.SapDocEntry, receipt.SapDocNum);
            result.Adopted++;

            logger.LogInformation(
                "Van sale {ExternalReference} was posted by another attempt as invoice {DocNum}; adopted it.",
                sale.ExternalReferenceId, receipt.SapDocNum);
            return;
        }

        // The handset's van_order is the business key all the way through, and SAP holds it in
        // U_Van_saleorder. Asking SAP first is what makes the 19:30 mop-up safe to run over sales the
        // 18:00 run may have posted just before losing its connection: an invoice that already exists is
        // adopted rather than posted again.
        var existing = await sapClient.GetInvoiceByVanSaleOrderAsync(sale.ExternalReferenceId, cancellationToken);
        if (existing is not null)
        {
            MarkPosted(sale, existing.DocEntry, existing.DocNum);
            result.Adopted++;

            // The caller saves the row after this returns, so the claim briefly records a document
            // the row does not. That gap is safe in the one direction that matters: an attempt
            // landing in it is answered by the replay branch above, which puts the same numbers back
            // on the sale. The reverse ordering has no such recovery.
            await claim.CompleteAsync(existing.DocEntry, existing.DocNum);

            logger.LogInformation(
                "Van sale {ExternalReference} was already in SAP as invoice {DocNum}; adopted it rather than posting again.",
                sale.ExternalReferenceId,
                existing.DocNum);
            return;
        }

        var grace = TimeSpan.FromMinutes(Math.Max(0, settings.Value.UnresolvedPostGraceMinutes));

        if (sale.PostIssuedAtUtc is { } issuedAt && DateTime.UtcNow - issuedAt < grace)
        {
            // A post went out for this sale very recently and SAP does not show an invoice for it.
            // That is exactly what a committed-but-not-yet-visible invoice looks like, so the
            // lookup's "no" cannot be trusted yet and the sale is not sent again.
            //
            // No attempt is spent: this is not the sale's fault and the wait is short. Once the
            // window passes, the lookup is trustworthy and the sale posts normally — so a post that
            // genuinely never landed still recovers on its own, just later.
            //
            // Van sales are the strictest case in the system. Each is already stamped with its own
            // ZIMRA receipt, so a second invoice is a second fiscal document for one sale.
            sale.LastPostingError =
                $"A post was issued for this sale at {sale.PostIssuedAtUtc:yyyy-MM-dd HH:mm:ss}Z and SAP has not "
                + "shown an invoice for it since. It has not been posted again, because SAP may hold the invoice "
                + $"already. Check SAP for U_Van_saleorder '{sale.ExternalReferenceId}'.";
            result.Failed++;
            result.Errors.Add($"{sale.ExternalReferenceId}: post issued, outcome unknown");

            logger.LogWarning(
                "Van sale {ExternalReference} had a post issued at {PostIssuedAt} that SAP does not yet show. "
                + "Waiting {Grace} before it may be sent again, in case SAP holds it already.",
                sale.ExternalReferenceId, sale.PostIssuedAtUtc, grace);
            return;
        }

        // What SAP shows in the invoice's Remarks, from the same builder the till route uses, so an
        // invoice reads the same whichever route posted it. Resolved before the post marker, so a
        // failed name lookup leaves nothing behind.
        var remarkNames = await DesktopSaleInvoiceRemarks.ResolveNamesAsync(
            context, sale, logger, cancellationToken);
        var request = DesktopSaleInvoiceRequestBuilder.Build(
            sale, DesktopSaleInvoiceRemarks.Build(sale, remarkNames));

        // Choose the batches the invoice issues from, before anything durable is written.
        //
        // SAP refuses a batch-managed line that names none and refuses the whole document with it,
        // and a van sells cheese. Nothing upstream can supply the selection — a handset sells by
        // item and DesktopSaleLineEntity has no batch column — so it is allocated here, FEFO
        // against the van's own warehouse.
        //
        // Before the post marker deliberately: allocation reads stock and creates nothing, so a
        // failure must not leave PostIssuedAtUtc set on a sale SAP has never heard of.
        var allocation = await InvoiceBatchAllocation.AllocateAsync(
            batchValidation, request, BatchAllocationStrategy.FEFO, cancellationToken);

        if (!allocation.IsValid)
        {
            // Thrown so it takes the same path as a SAP rejection, and worded by DescribeFailure so
            // an unread warehouse goes back on the queue rather than spending one of the six
            // attempts a van sale gets.
            throw new InvalidOperationException(
                InvoiceBatchAllocation.DescribeFailure(
                    allocation, $"van sale {sale.ExternalReferenceId}"));
        }

        // Durable before the request goes out. SapDocEntry is written from a reply that may never
        // arrive, so without this a post that commits and then times out leaves no local trace and
        // the next pass posts the sale a second time.
        sale.PostIssuedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(CancellationToken.None);

        Invoice invoice;
        try
        {
            invoice = await sapClient.CreateInvoiceAsync(request, cancellationToken);
        }
        catch (Exception ex) when (SapFailureClassifier.DefinitelyNotCommitted(ex))
        {
            // SAP answered, and the answer was no. Nothing exists, so the marker comes off and the
            // sale stays retryable; leaving it would park a sale that only needs its data fixed.
            sale.PostIssuedAtUtc = null;
            await context.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            // Anything else leaves the document's existence unknown, so neither deducting nor
            // leaving the reading alone can be justified. The rest of the run reads SAP again.
            readWindow.Discard();
            throw;
        }

        MarkPosted(sale, invoice.DocEntry, invoice.DocNum);
        result.Posted++;

        // SAP has the document, so the stock it names is gone. After the post and not before it:
        // what is deducted has to be what was issued, not what was asked for.
        readWindow.RecordIssued(allocation.AllocatedLines);
        await claim.CompleteAsync(invoice.DocEntry, invoice.DocNum);

        await RecordStockLeavingTheVanAsync(sale, invoice.DocNum, cancellationToken);

        logger.LogInformation(
            "Posted van sale {ExternalReference} (ZIMRA receipt {ReceiptGlobalNo}) to SAP as invoice {DocNum}.",
            sale.ExternalReferenceId,
            sale.ReceiptGlobalNo,
            invoice.DocNum);
    }

    /// <summary>
    /// Tells the stock ledger what this sale took off the van, and records it when the van did not
    /// have it.
    /// </summary>
    /// <remarks>
    /// <para><b>Settled, not asked.</b> The goods left the van when the customer was served, hours
    /// before this ran, and the receipt was signed to ZIMRA at that moment. There is no version of
    /// this that can refuse: the sale happened. Everything here can do is make the record match.</para>
    ///
    /// <para><b>What this changes.</b> Nothing decremented a van's stock at all — the snapshot a
    /// handset files each morning stayed at the morning count all day, so the shopkeeper-facing
    /// catalogue offered a van's full load after it had sold out, and an over-sale left no trace
    /// anywhere. The van stock report is unaffected: it reads OriginalQuantity, deliberately,
    /// because AvailableQuantity meant nothing for a van until now.</para>
    ///
    /// <para><b>Where an over-sale actually gets prevented.</b> Not here. The handset is the only
    /// place that knows before the goods move, and it is a separate application. This is the half
    /// that can be built on this side: make it visible, attributed, and countable on the day it
    /// happens rather than at a month-end reconciliation.</para>
    /// </remarks>
    private async Task RecordStockLeavingTheVanAsync(
        DesktopSaleEntity sale,
        int docNum,
        CancellationToken cancellationToken)
    {
        var taken = sale.Lines
            .Select(line => new StockLedgerLine(
                line.ItemCode,
                string.IsNullOrWhiteSpace(line.WarehouseCode) ? sale.WarehouseCode : line.WarehouseCode,
                line.Quantity))
            .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode)
                        && !string.IsNullOrWhiteSpace(line.WarehouseCode)
                        && line.Quantity > 0)
            .ToList();

        if (taken.Count == 0)
        {
            return;
        }

        try
        {
            var shortfalls = await stockLedger.TakeSettledAsync(
                taken, $"van sale {sale.ExternalReferenceId} (invoice {docNum})", cancellationToken);

            foreach (var shortfall in shortfalls)
            {
                context.StockLedgerDivergences.Add(new StockLedgerDivergenceEntity
                {
                    LedgerDay = stockLedger.CurrentLedgerDay,
                    Source = StockLedgerDivergenceSources.SettledDocument,
                    Reference = $"{sale.ExternalReferenceId} / invoice {docNum}",
                    WarehouseCode = shortfall.WarehouseCode,
                    ItemCode = shortfall.ItemCode,
                    LedgerQuantity = shortfall.Held,
                    SapIssuableQuantity = shortfall.Taken,
                    Difference = -shortfall.Excess
                });

                logger.LogWarning(
                    "Van sale {ExternalReference} sold {Taken} of {ItemCode} from {WarehouseCode}, which "
                    + "held {Held}. The van sold {Excess} more than its record says it was carrying.",
                    sale.ExternalReferenceId, shortfall.Taken, shortfall.ItemCode,
                    shortfall.WarehouseCode, shortfall.Held, shortfall.Excess);
            }
        }
        catch (Exception ex)
        {
            // The invoice is in SAP and the receipt is with ZIMRA; neither depends on this. A ledger
            // that missed the movement overstates the van until tomorrow's count, which is worth a
            // warning and is not worth failing a posted sale over.
            logger.LogWarning(ex,
                "Van sale {ExternalReference} posted as invoice {DocNum}, but the stock ledger was not told",
                sale.ExternalReferenceId, docNum);
        }
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

    /// <summary>
    /// Sales left alone because another post for them held the claim. Not a failure, and not counted
    /// in <see cref="Total"/>: nothing was attempted and nothing was written to the sale.
    /// </summary>
    public int InFlight { get; set; }

    public List<string> Errors { get; } = [];

    public int Total => Posted + Adopted + Failed;
}
