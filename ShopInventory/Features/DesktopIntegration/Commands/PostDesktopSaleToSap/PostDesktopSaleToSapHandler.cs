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
/// The bulk command is a loop over this one, for the same reason: every sale in a batch gets its own
/// claim, its own SAP lookup and its own outcome.
/// </para>
/// </remarks>
public sealed class PostDesktopSaleToSapHandler(
    ApplicationDbContext db,
    DesktopSalePostingService tillPostingService,
    VanSalesEndOfDayPostingService vanPostingService,
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
            .Select(s => new
            {
                s.Id,
                s.ExternalReferenceId,
                s.SourceSystem,
                s.WarehouseCode,
                s.ConsolidationStatus,
                s.FiscalizationStatus
            })
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
            sale.SourceSystem, sale.ConsolidationStatus, sale.FiscalizationStatus);

        if (refusal is not null)
        {
            return Errors.DesktopSales.SaleNotPostable(reference, refusal);
        }

        var isVanSale = string.Equals(sale.SourceSystem, SaleSourceSystems.VanSales, StringComparison.Ordinal);

        logger.LogInformation(
            "Posting sale {ExternalReference} to SAP on request via the {Route} route.",
            reference, isVanSale ? "van" : "till");

        var outcome = isVanSale
            ? Summarise(await vanPostingService.PostSaleAsync(sale.Id, cancellationToken))
            : Summarise(await tillPostingService.PostSaleAsync(sale.Id, cancellationToken));

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

    private enum PostKind { Posted, Adopted, InFlight, Failed, NotThisRoute }

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
