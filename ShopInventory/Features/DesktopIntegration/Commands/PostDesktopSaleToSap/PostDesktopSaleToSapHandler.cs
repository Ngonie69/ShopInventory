using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSaleToSap;

/// <summary>
/// Posts one sale to SAP on request, through whichever route owns it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. It decides <i>whether</i> this caller may post <i>this</i> sale and then hands
/// the sale to the service that already knows how — the same method the background pass calls, so
/// there is one implementation of "put this sale in SAP" and one place a duplicate could come from.
/// Rebuilding the post here would mean two guard chains that have to agree forever, and the one that
/// gets forgotten is the one that invoices a sale twice.
/// </para>
/// <para>
/// Three routes. A till sale posts through <see cref="DesktopSalePostingService"/> and an offline van
/// sale through <see cref="VanSalesEndOfDayPostingService"/>, each as an invoice of its own. An online
/// van sale's row is the receipt the device signed before SAP was asked, and its invoice is its
/// reservation's to produce, so it posts through <see cref="VanSaleFiscalFirstPoster"/> — the door the
/// handset's request and the invoice queue both use. That is the sale the van sales console most needs
/// a button for: SAP refused it after the receipt was signed, the queue parked it for review, and
/// nothing else offers it again.
/// </para>
/// <para>
/// The bulk command is a loop over this one, for the same reason: every sale in a batch gets its own
/// claim, its own SAP lookup and its own outcome.
/// </para>
/// </remarks>
public sealed class PostDesktopSaleToSapHandler(
    ApplicationDbContext db,
    DesktopSalePostingService tillPostingService,
    VanSalesEndOfDayPostingService vanPostingService,
    VanSaleFiscalFirstPoster onlineVanPoster,
    IAuditService auditService,
    ILogger<PostDesktopSaleToSapHandler> logger)
    : IRequestHandler<PostDesktopSaleToSapCommand, ErrorOr<DesktopSalePostResult>>
{
    public async Task<ErrorOr<DesktopSalePostResult>> Handle(
        PostDesktopSaleToSapCommand command,
        CancellationToken cancellationToken)
    {
        var reference = command.ExternalReferenceId.Trim();

        // The same scope the sales list resolves, applied to a write. A shop-scoped account that may
        // only read its own shop's takings must not be able to put another shop's in SAP.
        var caller = await db.Users
            .AsNoTracking()
            .Include(user => user.Shop)
            .FirstOrDefaultAsync(user => user.Id == command.CallerUserId, cancellationToken);

        var scope = DesktopSalesReadScopeResolver.Resolve(caller);
        if (scope.IsError)
        {
            return scope.Errors;
        }

        var sale = await db.DesktopSales
            .AsNoTracking()
            .Where(s => s.ExternalReferenceId == reference)
            .Select(s => new SaleRow(
                s.Id,
                s.ExternalReferenceId,
                s.SourceSystem,
                s.WarehouseCode,
                s.ConsolidationStatus,
                s.FiscalizationStatus,
                s.SapDocNum,
                s.DocDate,
                s.NumAtCard,
                s.Comments,
                s.AmountPaid))
            .FirstOrDefaultAsync(cancellationToken);

        if (sale is null)
        {
            return Errors.DesktopSales.SaleNotFound(reference);
        }

        if (!scope.Value.IsUnrestricted &&
            !string.Equals(sale.WarehouseCode, scope.Value.WarehouseCode, StringComparison.OrdinalIgnoreCase))
        {
            // Refused rather than ignored, the same way the read is. Quietly doing nothing would look
            // to the console exactly like a post that failed in SAP.
            return Errors.DesktopSales.SalesReadOutsideScope(sale.WarehouseCode, scope.Value.WarehouseCode!);
        }

        var refusal = DesktopSalePostEligibility.Refusal(
            sale.SourceSystem, sale.ConsolidationStatus, sale.FiscalizationStatus, sale.SapDocNum);

        if (refusal is not null)
        {
            return Errors.DesktopSales.SaleNotPostable(reference, refusal);
        }

        var route = RouteFor(sale.SourceSystem);

        logger.LogInformation(
            "Posting sale {ExternalReference} to SAP on request via the {Route} route.",
            reference, route);

        var outcome = route switch
        {
            Route.OnlineVan => await PostOnlineVanSaleAsync(sale, cancellationToken),
            Route.Van => Summarise(await vanPostingService.PostSaleAsync(sale.Id, cancellationToken)),
            _ => Summarise(await tillPostingService.PostSaleAsync(sale.Id, cancellationToken))
        };

        // Read back rather than inferred: the posting services are the only writers of these, and an
        // adopted invoice's numbers come from SAP rather than from anything this handler saw.
        var posted = await db.DesktopSales
            .AsNoTracking()
            .Where(s => s.Id == sale.Id)
            .Select(s => new { s.SapDocEntry, s.SapDocNum, s.LastPostingError })
            .FirstOrDefaultAsync(cancellationToken);

        await AuditAsync(reference, outcome, posted?.SapDocNum);

        return outcome.Kind switch
        {
            PostKind.Posted or PostKind.Adopted => new DesktopSalePostResult(
                reference,
                outcome.Kind == PostKind.Posted
                    ? DesktopSalePostOutcomes.Posted
                    : DesktopSalePostOutcomes.AlreadyInSap,
                posted?.SapDocEntry,
                posted?.SapDocNum,
                outcome.Kind == PostKind.Posted
                    ? $"Posted to SAP as invoice {posted?.SapDocNum}."
                    : $"SAP already held this sale as invoice {posted?.SapDocNum}."),

            PostKind.InFlight => Errors.DesktopSales.SalePostInProgress(reference),

            // The route read the row and found it was not one it could post after all. Eligibility
            // above should have caught every such case, so reaching here means the row changed under
            // us, and the route's own sentence says how.
            PostKind.NotPostable => Errors.DesktopSales.SaleNotPostable(
                reference, outcome.Error ?? "This sale cannot be posted."),

            // The route refused the sale outright — it read the row and decided it was not one of
            // its own. Eligibility above should have caught every such case, so reaching here means
            // the two disagree, and saying so plainly beats reporting a SAP failure that never
            // happened.
            PostKind.NotThisRoute => Errors.DesktopSales.SaleNotPostable(
                reference, "This sale is not one the posting route recognises."),

            _ => Errors.DesktopSales.SalePostFailed(
                reference,
                outcome.Error ?? posted?.LastPostingError ?? "SAP did not accept the invoice.")
        };
    }

    private enum Route { Till, Van, OnlineVan }

    private static Route RouteFor(string? sourceSystem) =>
        string.Equals(sourceSystem, SaleSourceSystems.VanSalesOnline, StringComparison.Ordinal) ? Route.OnlineVan
        : string.Equals(sourceSystem, SaleSourceSystems.VanSales, StringComparison.Ordinal) ? Route.Van
        : Route.Till;

    /// <summary>
    /// What the post needs to know about the row, and — for an online van sale — what its invoice is
    /// dated and captioned with.
    /// </summary>
    private sealed record SaleRow(
        int Id,
        string ExternalReferenceId,
        string? SourceSystem,
        string WarehouseCode,
        DesktopSaleConsolidationStatus ConsolidationStatus,
        DesktopSaleFiscalizationStatus FiscalizationStatus,
        int? SapDocNum,
        DateTime DocDate,
        string? NumAtCard,
        string? Comments,
        decimal AmountPaid);

    /// <summary>
    /// Posts an online van sale's receipt row through its reservation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The poster is handed a signed row, so it never asks the device: it reopens a reservation SAP's
    /// refusal closed, holds the stock while it posts, and confirms with fiscalisation off so the receipt
    /// is not filed a second time under the DocNum. Every guard against a second invoice — the Confirming
    /// claim and the <c>U_Van_saleorder</c> lookup — is the confirm's own.
    /// </para>
    /// <para>
    /// Dated the day the sale was made, as the queue dates it: an invoice dated today for goods that left
    /// last week misstates the takings of both days.
    /// </para>
    /// </remarks>
    private async Task<PostOutcome> PostOnlineVanSaleAsync(SaleRow sale, CancellationToken cancellationToken)
    {
        var reservation = await db.StockReservations
            .AsNoTracking()
            .Where(r => r.ExternalReferenceId == sale.ExternalReferenceId
                && r.SourceSystem == SaleSourceSystems.VanSales)
            .Select(r => new { r.ReservationId, r.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (reservation is null)
        {
            return new PostOutcome(
                PostKind.NotPostable,
                "This receipt has no reservation to post from, so its invoice has to be raised by hand or the receipt credited.");
        }

        // Answered before the poster is asked, so a person pressing Post while the queue's own confirm is
        // in flight is told so, rather than having the refusal counted as an attempt against the sale.
        if (string.Equals(reservation.Status, ReservationStatus.Confirming, StringComparison.Ordinal))
        {
            return new PostOutcome(PostKind.InFlight, null);
        }

        var day = sale.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var outcome = await onlineVanPoster.FiscaliseThenPostAsync(
            new VanSaleFiscalFirstRequest(
                reservation.ReservationId,
                DocDate: day,
                DocDueDate: day,
                NumAtCard: sale.NumAtCard,
                Comments: sale.Comments,
                AmountPaid: sale.AmountPaid,
                MayAlreadyBeFiscalised: true),
            cancellationToken);

        switch (outcome.Status)
        {
            case VanSaleFiscalFirstStatus.Posted:
                return new PostOutcome(outcome.Adopted ? PostKind.Adopted : PostKind.Posted, null);

            case VanSaleFiscalFirstStatus.AwaitingSap:
            {
                // SAP refused, or another caller took the reservation between the read above and the
                // confirm. The reservation says which: a claim still held is a post in flight.
                var status = await db.StockReservations
                    .AsNoTracking()
                    .Where(r => r.ReservationId == reservation.ReservationId)
                    .Select(r => r.Status)
                    .FirstOrDefaultAsync(CancellationToken.None);

                return string.Equals(status, ReservationStatus.Confirming, StringComparison.Ordinal)
                    ? new PostOutcome(PostKind.InFlight, null)
                    : new PostOutcome(PostKind.Failed, outcome.Error);
            }

            default:
                // NotPostable, or a fiscal outcome — which eligibility rules out for a signed row, so the
                // row changed under us. The poster's own sentence says how.
                return new PostOutcome(PostKind.NotPostable, outcome.Error);
        }
    }

    private enum PostKind { Posted, Adopted, InFlight, Failed, NotThisRoute, NotPostable }

    private sealed record PostOutcome(PostKind Kind, string? Error);

    private static PostOutcome Summarise(DesktopSalePostingRunResult? result) =>
        result is null
            ? new PostOutcome(PostKind.NotThisRoute, null)
            : Summarise(result.Posted, result.Adopted, result.InFlight, result.Errors);

    private static PostOutcome Summarise(VanSalesPostingRunResult? result) =>
        result is null
            ? new PostOutcome(PostKind.NotThisRoute, null)
            : Summarise(result.Posted, result.Adopted, result.InFlight, result.Errors);

    /// <remarks>
    /// One sale went in, so exactly one of these counters moved — except when none did, which is a
    /// failure the route recorded on the row rather than in the run.
    /// </remarks>
    private static PostOutcome Summarise(int posted, int adopted, int inFlight, List<string> errors)
    {
        if (posted > 0) return new PostOutcome(PostKind.Posted, null);
        if (adopted > 0) return new PostOutcome(PostKind.Adopted, null);
        if (inFlight > 0) return new PostOutcome(PostKind.InFlight, null);
        return new PostOutcome(PostKind.Failed, errors.FirstOrDefault());
    }

    /// <remarks>
    /// Audited whatever happened, and never allowed to break the post: the document is already in
    /// SAP by the time this runs, so a failed audit write must not turn a successful post into an
    /// error the operator will press Post again over.
    /// </remarks>
    private async Task AuditAsync(string reference, PostOutcome outcome, int? sapDocNum)
    {
        try
        {
            var succeeded = outcome.Kind is PostKind.Posted or PostKind.Adopted;

            await auditService.LogAsync(
                AuditActions.PostDesktopSaleToSAP,
                nameof(DesktopSaleEntity),
                reference,
                succeeded
                    ? $"Sale {reference} {(outcome.Kind == PostKind.Posted ? "posted" : "adopted")} as SAP invoice {sapDocNum}"
                    : $"Sale {reference} was not posted ({outcome.Kind})",
                succeeded,
                succeeded ? null : outcome.Error);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Failed to audit the SAP post of sale {ExternalReference}.", reference);
        }
    }
}
