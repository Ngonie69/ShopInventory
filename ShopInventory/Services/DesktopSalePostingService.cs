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
    IDesktopSalePostGuard postGuard,
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

    /// <summary>
    /// Posts one named sale to SAP now, on request, and reports what happened to it. Returns null
    /// when there is no such sale, or when it is not this route's to post.
    /// </summary>
    /// <remarks>
    /// What the manual and bulk "Post to SAP" levers call, and the counterpart to
    /// <c>VanSalesEndOfDayPostingService.PostSaleAsync</c> for the two till sources.
    ///
    /// <para>
    /// The attempt cap is deliberately not applied. It exists to stop an automatic pass re-offering
    /// a hopeless sale to SAP every minute forever, and a person pressing Post has said otherwise —
    /// usually because they have just fixed the thing SAP was refusing. The sales most in need of
    /// this are exactly the ones the cap has already parked, so honouring it here would make the
    /// button useless in the only case it exists for.
    /// </para>
    ///
    /// <para>
    /// Nothing else is relaxed. The claim, the SAP lookup and the unresolved-post grace window all
    /// still apply, because those prevent a second invoice rather than merely rationing attempts.
    /// </para>
    /// </remarks>
    public async Task<DesktopSalePostingRunResult?> PostSaleAsync(
        int saleId,
        CancellationToken cancellationToken = default)
    {
        var sale = await context.DesktopSales
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken);

        if (sale is null ||
            sale.SourceSystem is null ||
            !SaleSourceSystems.PostedByDesktopSaleJob.Contains(sale.SourceSystem))
        {
            return null;
        }

        var result = new DesktopSalePostingRunResult();

        // Answered without a doomed round trip, and without touching the sale. Letting it through
        // would overwrite LastPostingError with the circuit message, losing the SAP rejection that
        // is the reason somebody is looking at this sale in the first place.
        if (circuitState.ShouldShortCircuit(out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            result.Failed++;
            result.Errors.Add(
                $"{sale.ExternalReferenceId}: SAP is unavailable; the circuit is open for another {seconds} seconds.");

            logger.LogInformation(
                "Refused to post till sale {ExternalReference} on request: the SAP circuit is open for another {RetryAfter}.",
                sale.ExternalReferenceId, retryAfter);
            return result;
        }

        try
        {
            await PostOneAsync(sale, result, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordFailure(sale, ex, cancellationToken, settings.Value);
            result.Failed++;
            result.Errors.Add($"{sale.ExternalReferenceId}: {ex.Message}");

            logger.LogError(
                ex, "Failed to post till sale {ExternalReference} to SAP on request.", sale.ExternalReferenceId);
        }

        await context.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "Till sale {ExternalReference} was posted on request: {Posted} posted, {Adopted} already in SAP, {Failed} failed.",
            sale.ExternalReferenceId, result.Posted, result.Adopted, result.Failed);

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

        var invoice = await PostInvoiceAsync(sale, result, cancellationToken);

        // Outside the claim, deliberately. The claim guards the creation of one A/R document; a
        // payment that failed is retried on later passes long after the invoice's claim completed,
        // and holding it here would mean a completed claim replaying the invoice away and the
        // settlement never being attempted again. The payment has its own guards — the persisted
        // status, and what SAP says the invoice has been paid.
        await PostPaymentAsync(sale, invoice);
    }

    /// <summary>
    /// Puts the sale's A/R invoice in SAP, or establishes that SAP already holds it.
    /// </summary>
    /// <remarks>
    /// Everything here runs under a claim on the sale, so the background pass, a manual post and a
    /// bulk post exclude one another rather than each only excluding itself. Inside the claim the
    /// order is still the invariant it always was: ask SAP before posting, record what SAP said
    /// before doing anything else, and never let a cancellation land between issuing a document and
    /// writing down its number.
    /// </remarks>
    private async Task<Invoice?> PostInvoiceAsync(
        DesktopSaleEntity sale,
        DesktopSalePostingRunResult result,
        CancellationToken cancellationToken)
    {
        await using var claim = await postGuard.ClaimAsync(sale, cancellationToken);

        if (claim.Outcome == DesktopSalePostClaimOutcome.InFlight)
        {
            // Nothing is written to the sale. The other post owns this row's outcome, and
            // overwriting LastPostingError here would replace a real SAP rejection — the reason
            // somebody is looking at the sale — with a note about scheduling.
            result.InFlight++;

            logger.LogInformation(
                "Left till sale {ExternalReference} alone: a post for it is already in flight.",
                sale.ExternalReferenceId);
            return null;
        }

        if (claim.Receipt is { } receipt)
        {
            // An earlier post completed and this pass had not seen it yet. Take the document from
            // the claim rather than posting, then read the invoice back so the payment step still
            // has something to decide against.
            MarkInvoicePosted(sale, receipt.SapDocEntry, receipt.SapDocNum);
            result.Adopted++;
            await context.SaveChangesAsync(CancellationToken.None);

            logger.LogInformation(
                "Till sale {ExternalReference} was posted by another attempt as invoice {DocNum}; adopted it.",
                sale.ExternalReferenceId, receipt.SapDocNum);

            try
            {
                return await sapClient.GetInvoiceByDocEntryAsync(receipt.SapDocEntry, cancellationToken);
            }
            catch (Exception exception)
            {
                // The invoice is adopted and saved; only the settlement needs this read, and it is
                // retried on its own. Letting the failure out would report the whole sale as failed
                // and spend an attempt on it, having just established that SAP holds its invoice.
                logger.LogWarning(
                    exception,
                    "Adopted till sale {ExternalReference} as invoice {DocNum} but could not read it back "
                    + "to settle it. The payment is retried on a later pass.",
                    sale.ExternalReferenceId, receipt.SapDocNum);
                return null;
            }
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
            else if (sale.PostIssuedAtUtc is { } issuedAt
                     && DateTime.UtcNow - issuedAt < TimeSpan.FromMinutes(Math.Max(0, settings.Value.UnresolvedPostGraceMinutes)))
            {
                // A post went out very recently and SAP does not show an invoice for it. That is
                // exactly what a committed-but-not-yet-visible invoice looks like, so the lookup's
                // "no" cannot be trusted yet and the sale is not sent again — this is the window the
                // observed duplicates all fell inside.
                //
                // No attempt is spent: it is not the sale's fault and the wait is short. Once the
                // window passes, the lookup is trustworthy and the sale posts normally, so a post
                // that genuinely never landed still recovers on its own.
                RecordUnresolvedPost(sale, result, settings.Value);
            }
            else
            {
                // The last point at which abandoning the sale costs nothing.
                cancellationToken.ThrowIfCancellationRequested();

                postIssued = true;

                // Durable before the request goes out, and committed on its own. This is the record
                // that survives losing the reply — SapDocEntry is written from a reply that may
                // never arrive.
                sale.PostIssuedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(CancellationToken.None);

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

            // Only now, with the numbers written down. Completing earlier would let a crash between
            // the two leave a claim replaying a document the sale has no record of.
            if (existing is not null)
            {
                await claim.CompleteAsync(existing.DocEntry, existing.DocNum);
            }

            return existing;
        }
        catch (Exception ex) when (postIssued)
        {
            // SAP answered, and the answer was no. Nothing was created, so the marker must come off
            // or the sale would never be posted again — it would sit asking SAP about an invoice
            // that will never appear until its attempts ran out.
            if (SapFailureClassifier.DefinitelyNotCommitted(ex))
            {
                sale.PostIssuedAtUtc = null;
                await context.SaveChangesAsync(CancellationToken.None);
                throw;
            }

            // The post was issued and we do not know whether SAP took it. Ask on the same business key.
            var recovered = await TryFindAfterFailedPostAsync(sale, ex);
            if (recovered is null)
            {
                // The marker stays. The next pass will ask SAP again rather than post again.
                throw;
            }

            AdoptInvoice(sale, recovered, result);
            await context.SaveChangesAsync(CancellationToken.None);
            await claim.CompleteAsync(recovered.DocEntry, recovered.DocNum);
            return recovered;
        }
    }

    /// <summary>
    /// Whether SAP already holds an invoice for this sale.
    /// </summary>
    /// <remarks>
    /// An unanswerable lookup throws rather than returning null. Treating "I could not ask" as "it is
    /// not there" is how a sale gets invoiced twice.
    /// </remarks>
    /// <summary>
    /// Records a sale whose post was issued and whose outcome is still unknown, without posting it
    /// again.
    /// </summary>
    /// <remarks>
    /// An attempt is spent on purpose. Each pass re-asks SAP, so an invoice that was merely slow to
    /// become visible is adopted within a minute or two and this is never seen again; a sale still
    /// unresolved after the whole budget is one where the post genuinely vanished, and that is worth
    /// a person's attention rather than an indefinite quiet loop.
    /// </remarks>
    private void RecordUnresolvedPost(
        DesktopSaleEntity sale,
        DesktopSalePostingRunResult result,
        DesktopSalePostingSettings options)
    {
        sale.LastPostingError =
            $"A post was issued for this sale at {sale.PostIssuedAtUtc:yyyy-MM-dd HH:mm:ss}Z and SAP has not "
            + "shown an invoice for it since. It has not been posted again, because SAP may hold the invoice "
            + "already and a second one cannot be withdrawn from ZIMRA. Check SAP for "
            + $"U_Van_saleorder '{sale.ExternalReferenceId}'.";

        result.Failed++;
        result.Errors.Add($"{sale.ExternalReferenceId}: post issued, outcome unknown");

        logger.LogWarning(
            "Till sale {ExternalReference} had a post issued at {PostIssuedAt} that SAP does not yet show. "
            + "Waiting {Grace} minutes before it may be sent again, in case SAP holds it already.",
            sale.ExternalReferenceId, sale.PostIssuedAtUtc, options.UnresolvedPostGraceMinutes);
    }

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

    /// <summary>
    /// Sales left alone because another post for them held the claim.
    /// </summary>
    /// <remarks>
    /// Not a failure and not counted in <see cref="Total"/>: nothing was attempted and nothing was
    /// written to the sale. It is reported separately because a manual post has to be able to say
    /// "somebody is already posting this" rather than either lying about success or inventing an
    /// error the sale does not have.
    /// </remarks>
    public int InFlight { get; set; }

    public List<string> Errors { get; } = [];

    public int Total => Posted + Adopted + Failed;
}
