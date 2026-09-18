using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.PostQueuedVanInvoices;

/// <summary>
/// Posts each fiscalised van sale in the invoice queue to SAP through the reservation it was queued with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the reservation, not a post of its own.</b> Confirming the reservation is how an online van
/// sale reaches SAP when SAP is up, so a queued one arrives as the same document: the sale's own
/// <c>U_Van_saleorder</c>, the batches reserved for it, the stock ledger settled once. It also brings the
/// guards that make re-running this safe — the Confirming claim that stops two runs posting one sale, and
/// the lookup by <c>U_Van_saleorder</c> that adopts an invoice whose reply was lost rather than posting a
/// second.
/// </para>
/// <para>
/// <b>Never fiscalised again.</b> The sale was fiscalised before this point — by <c>InvoicePostingJob</c>
/// under its own reference, or on the handset that signed it — so the SAP invoice is posted with
/// fiscalisation off. Fiscalising it by its DocNum would lodge a second receipt for a sale the customer
/// already holds one for, and a fiscal receipt cannot be withdrawn. <c>PerSaleInvoiceRegistry</c> is what
/// stops anything downstream offering to do it.
/// </para>
/// <para>
/// <b>Deferred or reviewed, decided by the reservation.</b> A confirm that fails leaves the reservation
/// saying which kind of failure it was: returned to Pending for a transient SAP failure, still Confirming
/// while another caller posts it, Failed when SAP refused the document. The first two are tried again
/// later; anything else needs a person, and goes where the Exception Center shows it.
/// </para>
/// </remarks>
public sealed class PostQueuedVanInvoicesHandler(
    ApplicationDbContext db,
    IStockReservationService reservationService,
    ISAPServiceLayerClient sapClient,
    SapCircuitBreakerState sapCircuitBreakerState,
    DesktopCreditSapPoster creditPoster,
    ILogger<PostQueuedVanInvoicesHandler> logger
) : IRequestHandler<PostQueuedVanInvoicesCommand, ErrorOr<PostQueuedVanInvoicesResult>>
{
    /// <summary>
    /// How long a sale whose post was deferred waits before it is tried again. The job runs every few
    /// seconds, and without this a SAP that answers slowly but never trips the breaker would be asked
    /// for the same invoice on every run.
    /// </summary>
    internal static readonly TimeSpan DeferralDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a converted sale waits for its sales order to reach SAP before it is invoiced without a
    /// link to it. The order's own post retries with backoff, and an hour covers those retries.
    /// </summary>
    internal static readonly TimeSpan OrderPostWait = TimeSpan.FromHours(1);

    /// <summary>Matches the MaxLength on <see cref="InvoiceQueueEntity.LastError"/>.</summary>
    private const int MaxLastErrorLength = 2000;

    private enum Outcome { Posted, Deferred, SentForReview }

    public async Task<ErrorOr<PostQueuedVanInvoicesResult>> Handle(
        PostQueuedVanInvoicesCommand command,
        CancellationToken cancellationToken)
    {
        // Every confirm would be refused by the Service Layer anyway, and each one that tries adds to the
        // failures that are keeping the breaker open.
        if (sapCircuitBreakerState.IsOpen)
        {
            return new PostQueuedVanInvoicesResult(0, 0, 0);
        }

        var now = DateTime.UtcNow;

        var entries = await db.InvoiceQueue
            .AsTracking()
            .Where(queued => queued.Status == InvoiceQueueStatus.Fiscalized
                && queued.SourceSystem == SaleSourceSystems.VanSales
                && (queued.NextRetryAt == null || queued.NextRetryAt <= now))
            .OrderBy(queued => queued.CreatedAt)
            .Take(command.BatchSize)
            .ToListAsync(cancellationToken);

        int posted = 0, deferred = 0, sentForReview = 0;

        foreach (var entry in entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            switch (await PostAsync(entry, cancellationToken))
            {
                case Outcome.Posted: posted++; break;
                case Outcome.Deferred: deferred++; break;
                default: sentForReview++; break;
            }
        }

        if (entries.Count > 0)
        {
            logger.LogInformation(
                "Queued van sales run: {Posted} posted to SAP, {Deferred} deferred, {Review} sent for review",
                posted, deferred, sentForReview);
        }

        return new PostQueuedVanInvoicesResult(posted, deferred, sentForReview);
    }

    private async Task<Outcome> PostAsync(InvoiceQueueEntity entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.ReservationId))
        {
            return await SendForReviewAsync(
                entry,
                "This fiscalised van sale has no reservation to post from, so it cannot reach SAP from the queue.");
        }

        var (baseOrder, waitingOnOrder) = await ResolveBaseOrderAsync(entry, cancellationToken);
        if (waitingOnOrder is not null)
        {
            return await DeferAsync(entry, waitingOnOrder);
        }

        ConfirmReservationResponseDto confirmed;

        try
        {
            confirmed = await reservationService.ConfirmQueuedReservationAsync(
                new ConfirmReservationRequest
                {
                    ReservationId = entry.ReservationId,
                    // The day the sale was made, not the day the queue caught up with it. An invoice
                    // dated today for goods that left last week misstates the day's takings on both.
                    DocDate = AuditService.ToCAT(entry.CreatedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DocDueDate = AuditService.ToCAT(entry.CreatedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Comments = string.IsNullOrWhiteSpace(entry.Notes) ? null : entry.Notes,
                    Fiscalize = false
                },
                baseOrder,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not post queued van sale {ExternalReference} to SAP", entry.ExternalReference);
            return await DeferAsync(entry, ex.Message);
        }

        // Nothing past the confirm runs on the caller's token. If SAP accepted the invoice, recording that
        // is a durable obligation: an entry left Fiscalized over a posted invoice is only re-confirmed and
        // adopted next run, but an entry left without its DocNum is an invoice nothing points at meanwhile.
        if (confirmed.Success && confirmed.SAPDocNum is > 0)
        {
            await RecordPostedAsync(entry, confirmed.SAPDocEntry, confirmed.SAPDocNum.Value);
            return Outcome.Posted;
        }

        var reason = DescribeFailure(confirmed);

        var reservationStatus = await db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.ReservationId == entry.ReservationId)
            .Select(reservation => reservation.Status)
            .FirstOrDefaultAsync(CancellationToken.None);

        return reservationStatus is ReservationStatus.Pending or ReservationStatus.Confirming
            ? await DeferAsync(entry, reason)
            : await SendForReviewAsync(
                entry,
                $"SAP did not take this fiscalised van sale (reservation {reservationStatus ?? "missing"}): {reason}");
    }

    /// <summary>
    /// The SAP sales order a converted sale's invoice should be based on, or why the post has to wait for
    /// it. Neither, for a sale that was not converted from an order or whose order cannot be linked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A van sales order is posted to SAP by the mobile post-save queue, and a handset can convert it the
    /// moment it is approved — which can be before that post has landed. Posted straight away, the invoice
    /// would miss its order and SAP would keep the order open beside it for ever. So the sale waits for its
    /// order, for <see cref="OrderPostWait"/> from when it was queued, and is then posted without the link:
    /// the link is worth a wait, not a fiscalised sale's invoice.
    /// </para>
    /// <para>
    /// The same bound covers an order SAP cannot return right now. An order that can never be linked —
    /// another customer's, cancelled or closed — is not waited for at all.
    /// </para>
    /// </remarks>
    private async Task<(SAPSalesOrder? Order, string? WaitingBecause)> ResolveBaseOrderAsync(
        InvoiceQueueEntity entry,
        CancellationToken cancellationToken)
    {
        if (entry.SalesOrderId is not int salesOrderId)
        {
            return (null, null);
        }

        var local = await db.SalesOrders
            .AsNoTracking()
            .Where(order => order.Id == salesOrderId)
            .Select(order => new { order.OrderNumber, order.SAPDocEntry })
            .FirstOrDefaultAsync(cancellationToken);

        var mayStillWait = DateTime.UtcNow - entry.CreatedAt < OrderPostWait;

        if (local?.SAPDocEntry is not int docEntry)
        {
            if (local is not null && mayStillWait)
            {
                return (null, $"Waiting for sales order {local.OrderNumber} to reach SAP, so the invoice can be based on it.");
            }

            logger.LogWarning(
                "Queued van sale {ExternalReference} was converted from sales order {SalesOrderId}, which is not in SAP; it is invoiced without a link to the order",
                entry.ExternalReference, salesOrderId);
            return (null, null);
        }

        SAPSalesOrder? sapOrder;
        try
        {
            sapOrder = await sapClient.GetSalesOrderByDocEntryAsync(docEntry, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (mayStillWait)
            {
                return (null, $"Could not read sales order {local.OrderNumber} from SAP: {ex.Message}");
            }

            logger.LogWarning(
                ex,
                "Could not read sales order {OrderNumber} (DocEntry {DocEntry}) from SAP; queued van sale {ExternalReference} is invoiced without a link to it",
                local.OrderNumber, docEntry, entry.ExternalReference);
            return (null, null);
        }

        var unusableBecause = ConsolidatedInvoiceLines.WhyNotBaseable(sapOrder, entry.CustomerCode ?? string.Empty);
        if (unusableBecause is not null)
        {
            logger.LogWarning(
                "Queued van sale {ExternalReference} is invoiced without a link to sales order {OrderNumber} (DocEntry {DocEntry}) because {Reason}",
                entry.ExternalReference, local.OrderNumber, docEntry, unusableBecause);
            return (null, null);
        }

        return (sapOrder, null);
    }

    private async Task RecordPostedAsync(InvoiceQueueEntity entry, int? sapDocEntry, int sapDocNum)
    {
        entry.Status = InvoiceQueueStatus.Completed;
        entry.SapDocEntry = sapDocEntry?.ToString(CultureInfo.InvariantCulture);
        entry.SapDocNum = sapDocNum;
        entry.ProcessedAt = DateTime.UtcNow;
        entry.NextRetryAt = null;
        entry.LastError = null;

        // An online van sale queued while SAP was down stored the receipt its handset signed on a row of
        // its own, with no document on it because there was none yet. That row is what tells every reader
        // the invoice records a receipt already with ZIMRA, and it can only say so once it names the
        // invoice — so it is given the DocNum in the same save that completes the entry.
        var receiptRow = await db.DesktopSales
            .AsTracking()
            .FirstOrDefaultAsync(
                sale => sale.ExternalReferenceId == entry.ExternalReference
                    && sale.SourceSystem == SaleSourceSystems.VanSalesOnline
                    && sale.SapDocNum == null,
                CancellationToken.None);

        if (receiptRow is not null)
        {
            receiptRow.SapDocEntry = sapDocEntry;
            receiptRow.SapDocNum = sapDocNum;
            receiptRow.PostedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "Queued van sale {ExternalReference} posted to SAP as DocNum {DocNum} (queue entry {QueueId})",
            entry.ExternalReference, sapDocNum, entry.Id);

        // A sale sits on this queue precisely because it was fiscalised and SAP would not take its
        // invoice, which is the longest that window is ever open — so it is the likeliest place for a
        // return to have been credited against a receipt with no invoice behind it. The memo was
        // deferred for want of an invoice; there is one now. Advisory: the invoice exists, and letting
        // this throw would leave the entry incomplete and offer SAP a second invoice for one receipt.
        if (receiptRow is not null)
        {
            try
            {
                await creditPoster.SettleForSaleAsync(receiptRow.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Queued van sale {ExternalReference} posted as invoice {DocNum}, but the fiscal "
                    + "credits against it could not be raised in SAP.",
                    entry.ExternalReference, sapDocNum);
            }
        }
    }

    private async Task<Outcome> DeferAsync(InvoiceQueueEntity entry, string reason)
    {
        // Not a retry count. The sale is fiscalised and owed to SAP whatever happens; there is no number
        // of failed attempts after which it stops being owed.
        entry.LastError = Truncate(reason);
        entry.NextRetryAt = DateTime.UtcNow.Add(DeferralDelay);

        await db.SaveChangesAsync(CancellationToken.None);

        logger.LogWarning(
            "Queued van sale {ExternalReference} was not posted to SAP and will be tried again: {Reason}",
            entry.ExternalReference, reason);

        return Outcome.Deferred;
    }

    private async Task<Outcome> SendForReviewAsync(InvoiceQueueEntity entry, string reason)
    {
        entry.Status = InvoiceQueueStatus.RequiresReview;
        entry.LastError = Truncate(reason);
        entry.ProcessedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(CancellationToken.None);

        logger.LogError(
            "Queued van sale {ExternalReference} is fiscalised but could not be posted to SAP and needs review: {Reason}",
            entry.ExternalReference, reason);

        return Outcome.SentForReview;
    }

    private static string DescribeFailure(ConfirmReservationResponseDto confirmed)
    {
        var errors = confirmed.Errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToList();

        if (confirmed.Success)
        {
            return "The reservation reported success without a SAP document number.";
        }

        return errors.Count == 0
            ? confirmed.Message ?? "SAP did not accept the invoice."
            : $"{confirmed.Message} {string.Join("; ", errors)}".Trim();
    }

    private static string Truncate(string value) =>
        value.Length <= MaxLastErrorLength ? value : value[..MaxLastErrorLength];
}
