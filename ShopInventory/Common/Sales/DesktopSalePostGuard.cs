using ShopInventory.Common.Idempotency;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

public sealed class DesktopSalePostGuard(
    IIdempotencyRequestStore store,
    ILogger<DesktopSalePostGuard> logger) : IDesktopSalePostGuard
{
    /// <summary>
    /// One scope for both posting routes, keyed on the sale alone.
    /// </summary>
    /// <remarks>
    /// Not scoped per caller, and that is the point: two people pressing Post on the same sale, or a
    /// person pressing it while the background pass is mid-post, must collide. A key carrying who
    /// asked would let every one of them through.
    ///
    /// Van sales and till sales share the scope even though separate services post them, because a
    /// sale belongs to exactly one route and the key is unique across the table — so sharing costs
    /// nothing and means a future third route cannot quietly get its own unguarded namespace.
    /// </remarks>
    private const string IdempotencyScope = "desktop-sale-post";

    public async Task<DesktopSalePostClaim> ClaimAsync(
        DesktopSaleEntity sale,
        CancellationToken cancellationToken)
    {
        var reference = sale.ExternalReferenceId;

        var acquired = await store.TryAcquireAsync<DesktopSalePostReceipt>(
            IdempotencyScope,
            reference,
            // Stable across every caller, or the store would read a manual post and a background
            // pass as different requests for the same key and answer the second RequestMismatch.
            new { ExternalReferenceId = reference },
            cancellationToken);

        switch (acquired.Outcome)
        {
            case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is { } receipt:
                logger.LogInformation(
                    "Sale {ExternalReference} was already posted to SAP as invoice {DocNum}; replaying it rather than posting again.",
                    reference, receipt.SapDocNum);
                return DesktopSalePostClaim.ForReplay(reference, receipt);

            // A completed claim that stored nothing usable. Treated as in flight rather than as a
            // grant: the claim exists, so a post under this key got far enough to complete, and
            // sending a second one on the strength of a missing payload is the one mistake this
            // whole mechanism is here to prevent.
            case IdempotencyAcquireOutcome.ReplayAvailable:
            case IdempotencyAcquireOutcome.InProgress:
            case IdempotencyAcquireOutcome.RequestMismatch:
                logger.LogWarning(
                    "A SAP post for sale {ExternalReference} is already claimed ({Outcome}); refusing to start a second.",
                    reference, acquired.Outcome);
                return DesktopSalePostClaim.ForInFlight(reference);

            default:
                return DesktopSalePostClaim.ForGrant(reference, acquired.RequestId!.Value, store, logger);
        }
    }
}
