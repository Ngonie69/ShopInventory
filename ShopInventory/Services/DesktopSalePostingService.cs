using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Posts shop till and vending sales to SAP, one A/R invoice and one incoming payment per sale.
///
/// A till fiscalises the moment the customer pays, so the receipt is printed and handed over long
/// before SAP hears about it. That is deliberate: a SAP round trip takes seconds the queue at the
/// counter does not have. This service closes the gap afterwards, within a minute or so.
///
/// One invoice per sale, never consolidated. Each sale already carries exactly one ZIMRA receipt, and
/// folding several into one SAP document would leave every document mapping to several receipts and no
/// receipt mapping to a document — which is the join the SAP-to-FDMS reconciliation needs. It is also
/// what lets each sale carry its own tender, instead of a day's takings being booked under whichever
/// tender happened to be most common.
///
/// Nothing here is safe to run twice by accident, so the ordering in <see cref="PostOneAsync"/> is the
/// invariant: ask SAP before posting, record what SAP said before doing anything else, and never let a
/// cancellation land between issuing a document and writing down its number.
/// </summary>
public sealed class DesktopSalePostingService(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    SapCircuitBreakerState circuitState,
    IOptions<DesktopSalePostingSettings> settings,
    IOptions<SAPSettings> sapSettings,
    ILogger<DesktopSalePostingService> logger)
{
    public async Task<DesktopSalePostingRunResult> PostPendingSalesAsync(
        CancellationToken cancellationToken = default)
    {
        var options = settings.Value;
        var result = new DesktopSalePostingRunResult();

        // Do not start a pass while SAP is known to be down. This job fires every minute, so without
        // this an outage would burn every sale's attempt budget in a few minutes and park a whole day's
        // takings behind a human.
        if (circuitState.ShouldShortCircuit(out var retryAfter))
        {
            logger.LogInformation(
                "Skipping desktop sale posting: the SAP circuit is open for another {RetryAfter}.", retryAfter);
            return result;
        }

        // A window rather than a single trading date. A till sale should reach SAP within minutes, and
        // one that failed at 23:55 must not be stranded when the date rolls over.
        var cutoff = DateTime.UtcNow.Date.AddDays(-options.LookbackDays);

        var pending = await context.DesktopSales
            .Include(s => s.Lines)
            .Where(s => s.DocDate >= cutoff &&
                        s.SourceSystem != null &&
                        SaleSourceSystems.PostedByDesktopSaleJob.Contains(s.SourceSystem) &&
                        s.PostingAttempts < options.MaxPostingAttempts &&
                        // Either the invoice has not been posted yet, or it has and its payment
                        // failed. Without the second case a failed payment would never be retried:
                        // posting the invoice sets Consolidated, which takes the sale out of the
                        // first case forever, leaving a real open A/R document nobody comes back to.
                        ((s.ConsolidationStatus == DesktopSaleConsolidationStatus.Pending &&
                          // Ready to be invoiced. Pending means vending has not fiscalised it yet, and
                          // Failed means it needs a human — posting either would put an invoice in SAP
                          // for a sale that has no receipt. Skipped is not the same thing: it is what a
                          // sale gets when fiscalisation was not asked for or is switched off, so it
                          // will never become Success and must still reach SAP, as it did before this
                          // route existed.
                          (s.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success ||
                           s.FiscalizationStatus == DesktopSaleFiscalizationStatus.Skipped)) ||
                         (s.ConsolidationStatus == DesktopSaleConsolidationStatus.Consolidated &&
                          s.PaymentStatus == DesktopSalePaymentStatuses.Failed)))
            .OrderBy(s => s.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return result;
        }

        logger.LogInformation("Posting {Count} till sales to SAP.", pending.Count);

        foreach (var sale in pending)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Stop cleanly rather than half-posting the tail. Whatever is left stays Pending and
                // the next pass, a minute later, picks it up.
                logger.LogWarning(
                    "Desktop sale posting was cancelled after {Done} of {Total}.", result.Total, pending.Count);
                break;
            }

            try
            {
                await PostOneAsync(sale, result, cancellationToken);
            }
            catch (Exception ex)
            {
                RecordFailure(sale, ex, cancellationToken, options);
                result.Failed++;
                result.Errors.Add($"{sale.ExternalReferenceId}: {ex.Message}");

                logger.LogError(
                    ex,
                    "Failed to post till sale {ExternalReference} to SAP. Attempt {Attempt} of {Max}.",
                    sale.ExternalReferenceId,
                    sale.PostingAttempts,
                    options.MaxPostingAttempts);
            }

            // Saved per sale, not once at the end. A crash mid-batch must never lose a DocEntry SAP has
            // already issued — that sale would be posted again on the next pass with nothing to show it
            // had succeeded.
            await context.SaveChangesAsync(CancellationToken.None);
        }

        logger.LogInformation(
            "Till sale posting finished: {Posted} posted, {Adopted} already in SAP, {Failed} failed.",
            result.Posted, result.Adopted, result.Failed);

        return result;
    }

    private async Task PostOneAsync(
        DesktopSaleEntity sale,
        DesktopSalePostingRunResult result,
        CancellationToken cancellationToken)
    {
        // A sale already invoiced, back only because its payment failed. Re-read the invoice — it is
        // what the duplicate-payment guard is decided on — and go straight to settling it. Falling
        // through to the posting path would ask SAP to invoice it a second time.
        if (sale.ConsolidationStatus == DesktopSaleConsolidationStatus.Consolidated &&
            sale.SapDocEntry.HasValue)
        {
            var posted = await sapClient.GetInvoiceByDocEntryAsync(sale.SapDocEntry.Value, cancellationToken);
            await PostPaymentAsync(sale, posted);
            return;
        }

        var postIssued = false;

        try
        {
            // Ask SAP first, before anything that can fail for an ordinary reason. A guard placed after
            // validation is unreachable in exactly the case it exists for: the sale fails earlier, no
            // post is issued, and the post-failure recovery below never runs either.
            var existing = await FindAlreadyPostedAsync(sale, cancellationToken);
            if (existing is not null)
            {
                AdoptInvoice(sale, existing, result);
            }
            else
            {
                // The last point at which abandoning the sale costs nothing.
                cancellationToken.ThrowIfCancellationRequested();

                postIssued = true;

                // CancellationToken.None, deliberately and literally: once the request is in flight, a
                // shutdown must not stop us from learning the DocEntry SAP just issued.
                var invoice = await sapClient.CreateInvoiceAsync(
                    DesktopSaleInvoiceRequestBuilder.Build(sale), CancellationToken.None);

                MarkInvoicePosted(sale, invoice.DocEntry, invoice.DocNum);
                result.Posted++;

                logger.LogInformation(
                    "Posted till sale {ExternalReference} to SAP as invoice {DocNum}.",
                    sale.ExternalReferenceId, invoice.DocNum);

                existing = invoice;
            }

            // The invoice number must be durable before a payment is risked against it.
            await context.SaveChangesAsync(CancellationToken.None);

            await PostPaymentAsync(sale, existing);
        }
        catch (Exception ex) when (postIssued)
        {
            // The post was issued and we do not know whether SAP took it. Ask on the same business key.
            var recovered = await TryFindAfterFailedPostAsync(sale, ex);
            if (recovered is null)
            {
                throw;
            }

            AdoptInvoice(sale, recovered, result);
            await context.SaveChangesAsync(CancellationToken.None);
            await PostPaymentAsync(sale, recovered);
        }
    }

    /// <summary>
    /// Whether SAP already holds an invoice for this sale.
    /// </summary>
    /// <remarks>
    /// An unanswerable lookup throws rather than returning null. Treating "I could not ask" as "it is
    /// not there" is how a sale gets invoiced twice.
    /// </remarks>
    private async Task<Invoice?> FindAlreadyPostedAsync(
        DesktopSaleEntity sale, CancellationToken cancellationToken)
    {
        try
        {
            return await sapClient.GetInvoiceByVanSaleOrderAsync(sale.ExternalReferenceId, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Not posting till sale {sale.ExternalReferenceId}: SAP could not be asked whether it " +
                $"already holds this invoice. {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Looks for a document a failed post may still have created. Swallows its own failure so the
    /// original error is the one the operator sees.
    /// </summary>
    private async Task<Invoice?> TryFindAfterFailedPostAsync(DesktopSaleEntity sale, Exception cause)
    {
        try
        {
            var recovered = await sapClient.GetInvoiceByVanSaleOrderAsync(
                sale.ExternalReferenceId, CancellationToken.None);

            if (recovered is not null)
            {
                logger.LogWarning(
                    cause,
                    "Posting till sale {ExternalReference} reported a failure but SAP holds invoice {DocNum}; adopting it.",
                    sale.ExternalReferenceId, recovered.DocNum);
            }

            return recovered;
        }
        catch (Exception lookupFailure)
        {
            logger.LogError(
                lookupFailure,
                "Could not check whether till sale {ExternalReference} reached SAP after a failed post.",
                sale.ExternalReferenceId);
            return null;
        }
    }

    private void AdoptInvoice(DesktopSaleEntity sale, Invoice invoice, DesktopSalePostingRunResult result)
    {
        MarkInvoicePosted(sale, invoice.DocEntry, invoice.DocNum);
        result.Adopted++;

        logger.LogInformation(
            "Till sale {ExternalReference} was already in SAP as invoice {DocNum}; adopted it rather than posting again.",
            sale.ExternalReferenceId, invoice.DocNum);
    }

    private static void MarkInvoicePosted(DesktopSaleEntity sale, int docEntry, int docNum)
    {
        sale.SapDocEntry = docEntry;
        sale.SapDocNum = docNum;
        sale.PostedAt = DateTime.UtcNow;
        sale.PostingAttempts++;
        sale.LastPostingError = null;
        sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
    }

    /// <summary>
    /// Settles the invoice with the tender the customer actually paid with.
    /// </summary>
    /// <remarks>
    /// Failing here leaves a real, open A/R invoice rather than an inconsistency: the sale stays
    /// Consolidated so the invoice is never posted again, and the payment is retried on its own.
    ///
    /// There is no SAP-side idempotency for payments — ClientRequestId is not forwarded to the Service
    /// Layer and there is no lookup by reference — so the only defences are the persisted status and
    /// what the invoice says it has been paid.
    /// </remarks>
    private async Task PostPaymentAsync(DesktopSaleEntity sale, Invoice? invoice)
    {
        if (invoice is null || sale.SapDocEntry is null)
        {
            return;
        }

        // Only null or a previous failure is retryable. Anything else means a payment was already sent
        // or deliberately withheld.
        if (sale.PaymentStatus is not (null or DesktopSalePaymentStatuses.Failed))
        {
            return;
        }

        if (sale.AmountPaid <= 0)
        {
            return;
        }

        if (invoice.PaidToDate >= sale.AmountPaid)
        {
            // SAP already shows this invoice settled. Without a business key to probe on, this is the
            // only thing standing between a lost reply and a duplicate payment.
            sale.PaymentStatus = DesktopSalePaymentStatuses.PostedUnconfirmed;
            sale.PaymentPostedAt = DateTime.UtcNow;
            sale.LastPaymentError = null;

            logger.LogWarning(
                "Till sale {ExternalReference}: SAP invoice {DocNum} is already settled, so no payment was sent.",
                sale.ExternalReferenceId, invoice.DocNum);
            return;
        }

        var built = SaleIncomingPaymentRequestBuilder.Build(
            sale, sale.SapDocEntry.Value, sapSettings.Value.SwipeCreditCardCode);

        if (!built.CanPost)
        {
            // Not a failure to retry — nothing about waiting makes an unmappable tender mappable. It
            // sits visible until a human maps it or the card code is configured.
            sale.PaymentStatus = DesktopSalePaymentStatuses.Unmapped;
            sale.LastPaymentError = Truncate(built.Reason, 2000);

            logger.LogWarning(
                "Till sale {ExternalReference} was invoiced but not settled: {Reason}",
                sale.ExternalReferenceId, built.Reason);
            return;
        }

        try
        {
            var payment = await sapClient.CreateIncomingPaymentAsync(built.Request!, CancellationToken.None);

            sale.PaymentSapDocEntry = payment.DocEntry;
            sale.PaymentSapDocNum = payment.DocNum;
            sale.PaymentPostedAt = DateTime.UtcNow;
            sale.PaymentStatus = DesktopSalePaymentStatuses.Posted;
            sale.LastPaymentError = null;

            logger.LogInformation(
                "Settled till sale {ExternalReference} with SAP payment {DocNum}.",
                sale.ExternalReferenceId, payment.DocNum);
        }
        catch (Exception ex)
        {
            sale.PaymentStatus = DesktopSalePaymentStatuses.Failed;
            sale.LastPaymentError = Truncate(ex.Message, 2000);

            logger.LogError(
                ex,
                "Till sale {ExternalReference} posted as invoice {DocNum} but its payment failed.",
                sale.ExternalReferenceId, sale.SapDocNum);
        }
    }

    /// <remarks>
    /// A transient failure does not spend an attempt. This job runs every minute, so counting a SAP
    /// outage against the budget would exhaust it on every sale within minutes and turn a network blip
    /// into a morning of manual work.
    ///
    /// The van route does the same, and used not to: it ran twice a night, where six attempts spanned
    /// days and an outage could not plausibly consume them. Once it gained a half-hourly pass that
    /// stopped being true, and the two now share this rule rather than one being an exception to it.
    /// </remarks>
    private static void RecordFailure(
        DesktopSaleEntity sale,
        Exception ex,
        CancellationToken cancellationToken,
        DesktopSalePostingSettings options)
    {
        // The wording is preserved rather than reworded: the exception centre classifies circuit-open
        // failures by matching on it.
        sale.LastPostingError = Truncate(ex.Message, 2000);

        if (!SapFailureClassifier.IsTransient(ex, cancellationToken))
        {
            sale.PostingAttempts++;
        }

        _ = options;
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// The values <see cref="DesktopSaleEntity.PaymentStatus"/> takes.
/// </summary>
public static class DesktopSalePaymentStatuses
{
    public const string Posted = "Posted";

    /// <summary>SAP already showed the invoice settled, so nothing was sent.</summary>
    public const string PostedUnconfirmed = "PostedUnconfirmed";

    public const string Failed = "Failed";

    /// <summary>The tender has no SAP payment means, or a swipe has no configured card code.</summary>
    public const string Unmapped = "Unmapped";
}

/// <summary>
/// What one posting pass did. <see cref="Adopted"/> is kept apart from <see cref="Posted"/> because
/// they mean different things: adopting means an earlier pass reached SAP without recording it, which
/// is worth noticing if it happens often.
/// </summary>
public sealed class DesktopSalePostingRunResult
{
    public int Posted { get; set; }
    public int Adopted { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = [];

    public int Total => Posted + Adopted + Failed;
}
