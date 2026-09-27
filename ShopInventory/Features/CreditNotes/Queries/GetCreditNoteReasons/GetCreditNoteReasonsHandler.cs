using ErrorOr;
using MediatR;
using Microsoft.Extensions.Caching.Hybrid;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Queries.GetCreditNoteReasons;

public sealed class GetCreditNoteReasonsHandler(
    ISAPServiceLayerClient sapClient,
    HybridCache cache,
    ILogger<GetCreditNoteReasonsHandler> logger
) : IRequestHandler<GetCreditNoteReasonsQuery, ErrorOr<GetCreditNoteReasonsResult>>
{
    private const string CacheKey = "creditnotes.reasons";

    /// <summary>
    /// How long the list is held before SAP is asked again.
    /// </summary>
    /// <remarks>
    /// The list is administered in SAP and changes a few times a year, while this is read every time
    /// somebody opens the cancel dialog. SAP's request pool is six process-wide slots shared with
    /// every interactive user, so re-reading it per dialog would spend a scarce resource on an
    /// answer that is the same all day.
    /// </remarks>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    private static readonly HybridCacheEntryOptions EntryOptions = new()
    {
        Expiration = CacheLifetime,
        LocalCacheExpiration = CacheLifetime
    };

    public async Task<ErrorOr<GetCreditNoteReasonsResult>> Handle(
        GetCreditNoteReasonsQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            // An empty list is cached too. A company database with no reason field will not grow one
            // between two dialogs, and not caching it would mean a SAP round trip on every open for
            // the one configuration where the answer is certainly nothing. A SAP failure is not: it
            // throws out of the loader, so HybridCache stores nothing and the next open asks again.
            return await cache.GetOrCreateAsync(
                CacheKey,
                sapClient,
                static async (client, token) =>
                {
                    var reasons = await client.GetCreditNoteLineReasonsAsync(token);
                    return new GetCreditNoteReasonsResult(
                        reasons.Select(reason => new CreditNoteReasonOption(reason.Value, reason.Description)).ToList());
                },
                EntryOptions,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read credit note reasons from SAP");
            return Errors.CreditNote.ReasonsUnavailable(ex.Message);
        }
    }
}
