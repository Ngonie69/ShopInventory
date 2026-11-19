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

    /// <summary>For handlers that never save a draft here: being asked to message anyone is a failure.</summary>
    public static ISapDraftApprovalNotice NoNotice() => new RecordingNotice { Unexpected = true };

    /// <summary>Records each B1 inbox message a handler asks for, and answers as if it went to "Whouse".</summary>
    internal sealed class RecordingNotice : ISapDraftApprovalNotice
    {
        public bool Unexpected { get; init; }
        public List<(SAPStockTransferDraft Draft, SapTransferRoute Route, string About, string? OrderNumber)> Sent { get; } = [];

        public Task<SapDraftNoticeResult> SendAsync(
            SAPStockTransferDraft draft,
            SapTransferRoute route,
            string about,
            string? orderNumber,
            CancellationToken cancellationToken)
        {
            if (Unexpected)
                throw new InvalidOperationException("No SAP draft is saved in this test, so no approver should be messaged.");
            Sent.Add((draft, route, about, orderNumber));
            return Task.FromResult(new SapDraftNoticeResult(["Whouse"], [], null));
        }
    }

    private sealed class NoTemplate : ISapTransferApprovalRoutes
    {
        private static readonly SapTransferRoute NotCovered = new(SapTransferRouteKind.NotCovered, []);

        public Task<SapTransferRoute> ForRequestAsync(InventoryTransferRequest request, CancellationToken cancellationToken)
            => Task.FromResult(NotCovered);

        public Task<SapTransferRoute> ForRouteAsync(
            string? fromWarehouse,
            string? toWarehouse,
            IEnumerable<string?> lineWarehouses,
            CancellationToken cancellationToken)
            => Task.FromResult(NotCovered);

        public Task<SapTransferRoute?> TryForRouteAsync(
            string? fromWarehouse,
            string? toWarehouse,
            IEnumerable<string?> lineWarehouses,
            CancellationToken cancellationToken)
            => Task.FromResult<SapTransferRoute?>(NotCovered);
    }
}
