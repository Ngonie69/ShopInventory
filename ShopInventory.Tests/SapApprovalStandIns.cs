using ShopInventory.Features.InventoryTransfers.SapApproval;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Stand-ins for SAP's own transfer approval, for tests about something else: no SAP template covers
/// any route, so every conversion posts as it did before SAP's approval was respected.
/// </summary>
internal static class SapApprovalStandIns
{
    public static ISapTransferApprovalRoutes NoTemplateCovers() => new NoTemplate();

    private sealed class NoTemplate : ISapTransferApprovalRoutes
    {
        private static readonly SapTransferRoute NotCovered = new(SapTransferRouteKind.NotCovered, []);

        public Task<SapTransferRoute> ForRequestAsync(InventoryTransferRequest request, CancellationToken cancellationToken)
            => Task.FromResult(NotCovered);

        public Task<SapTransferRoute?> TryForRouteAsync(
            string? fromWarehouse,
            string? toWarehouse,
            IEnumerable<string?> lineWarehouses,
            CancellationToken cancellationToken)
            => Task.FromResult<SapTransferRoute?>(NotCovered);
    }
}
