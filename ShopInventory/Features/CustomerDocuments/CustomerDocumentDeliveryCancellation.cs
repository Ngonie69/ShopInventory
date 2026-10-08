using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>
/// Withdraws deliveries that have not gone yet, when the number they are for stops being one to send to.
/// </summary>
/// <remarks>
/// Only the waiting states are touched, in one conditional update, so a delivery a pass has already
/// claimed — preparing or sending — is left to finish rather than having its state pulled from under it.
/// The pass re-checks consent after claiming, so it stops one of those itself.
/// </remarks>
internal static class CustomerDocumentDeliveryCancellation
{
    public static Task<int> CancelWaitingAsync(
        ApplicationDbContext context,
        Expression<Func<CustomerDocumentDeliveryEntity, bool>> which,
        string reason,
        string closedBy,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        context.CustomerDocumentDeliveries
            .Where(which)
            .Where(delivery => delivery.Status == CustomerDocumentDeliveryStatus.Pending
                || delivery.Status == CustomerDocumentDeliveryStatus.WaitingForFiscal
                || delivery.Status == CustomerDocumentDeliveryStatus.Held)
            .ExecuteUpdateAsync(update => update
                .SetProperty(delivery => delivery.Status, CustomerDocumentDeliveryStatus.Cancelled)
                .SetProperty(delivery => delivery.StatusReason, reason)
                .SetProperty(delivery => delivery.ClosedAtUtc, nowUtc)
                .SetProperty(delivery => delivery.ClosedBy, closedBy)
                .SetProperty(delivery => delivery.UpdatedAtUtc, nowUtc),
                cancellationToken);
}
