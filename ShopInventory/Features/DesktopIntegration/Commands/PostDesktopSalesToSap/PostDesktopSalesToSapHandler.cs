using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSaleToSap;

namespace ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSalesToSap;

/// <summary>
/// Posts each named sale in turn, and reports what happened to every one of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a loop over the single command rather than a batch of its own.</b> Every guard that keeps
/// a sale from being invoiced twice is per sale — the claim, the <c>U_Van_saleorder</c> lookup, the
/// unresolved-post grace window — and a batch path would either re-implement them or skip them. So
/// this adds no posting logic at all; it decides the order, stops when asked to, and collects
/// outcomes.
/// </para>
/// <para>
/// <b>Sequential on purpose.</b> Posting these in parallel would multiply this one operator's
/// pressure on the six process-wide SAP slots, and those slots are shared with every interactive
/// user in the building. A bulk post is a background chore; it does not get to starve the people
/// waiting on a page.
/// </para>
/// <para>
/// <b>Re-pressing after a timeout is safe, and is the intended remedy.</b> A long batch can outlive
/// the caller's HTTP timeout while continuing to post. Sent again, every sale that made it replays
/// its document from the claim instead of raising a second, and the rest post normally.
/// </para>
/// </remarks>
public sealed class PostDesktopSalesToSapHandler(
    IMediator mediator,
    ILogger<PostDesktopSalesToSapHandler> logger)
    : IRequestHandler<PostDesktopSalesToSapCommand, ErrorOr<DesktopSalesBulkPostResult>>
{
    public async Task<ErrorOr<DesktopSalesBulkPostResult>> Handle(
        PostDesktopSalesToSapCommand command,
        CancellationToken cancellationToken)
    {
        // Duplicates in one request are the same sale twice; the claim would answer the second with
        // a replay, but reporting the same reference on two rows is a worse answer than reporting it
        // once. Order is the caller's, so the console's list order is what the operator watches.
        var references = command.ExternalReferenceIds
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (references.Count == 0)
        {
            return Errors.DesktopSales.BulkPostReferencesRequired;
        }

        logger.LogInformation("Posting {Count} sales to SAP in one request.", references.Count);

        var results = new List<DesktopSalePostResult>(references.Count);

        foreach (var reference in references)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Stop cleanly rather than half-posting the tail. Everything already posted is
                // durable, and the sales not reached were never touched, so re-pressing finishes
                // the job without risking a second document for any of them.
                logger.LogWarning(
                    "Bulk SAP post was cancelled after {Done} of {Total} sales.",
                    results.Count, references.Count);
                break;
            }

            var outcome = await mediator.Send(
                new PostDesktopSaleToSapCommand(command.CallerUserId, reference), cancellationToken);

            if (!outcome.IsError)
            {
                results.Add(outcome.Value);
                continue;
            }

            var error = outcome.FirstError;

            // An account that may not post this sale at all is answered as a refusal of the whole
            // request, not as one row among many. It is a statement about the caller rather than
            // about the sale, so it will be true of every other reference too — and burying it in a
            // list of outcomes would report a permissions failure as a data problem.
            if (error.Type is ErrorType.Forbidden or ErrorType.Unauthorized)
            {
                return outcome.Errors;
            }

            results.Add(ToRow(reference, error));
        }

        var summary = new DesktopSalesBulkPostResult(results);

        logger.LogInformation(
            "Bulk SAP post finished: {Posted} posted, {Adopted} already in SAP, {InProgress} already being posted, "
            + "{Failed} failed, {NotPostable} not postable.",
            summary.Posted, summary.AlreadyInSap, summary.InProgress, summary.Failed, summary.NotPostable);

        return summary;
    }

    /// <remarks>
    /// Matched on the error code rather than on its type, because two of these are the same
    /// <see cref="ErrorType"/> and mean quite different things to whoever is reading the list: a
    /// sale nobody may post yet is not a sale SAP refused.
    /// </remarks>
    private static DesktopSalePostResult ToRow(string reference, Error error)
    {
        var outcome = error.Code switch
        {
            "DesktopSales.SalePostInProgress" => DesktopSalePostOutcomes.InProgress,
            "DesktopSales.SaleNotPostable" => DesktopSalePostOutcomes.NotPostable,
            "DesktopSales.SaleNotFound" => DesktopSalePostOutcomes.NotPostable,
            _ => DesktopSalePostOutcomes.Failed
        };

        // The reference is already the head of every one of these messages, so it is stripped rather
        // than repeated — the row states it in its own column.
        var message = error.Description.StartsWith($"{reference}: ", StringComparison.Ordinal)
            ? error.Description[(reference.Length + 2)..]
            : error.Description;

        return new DesktopSalePostResult(reference, outcome, null, null, message);
    }
}
