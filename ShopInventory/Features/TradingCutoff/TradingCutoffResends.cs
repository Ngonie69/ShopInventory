using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.TradingCutoff;

/// <summary>
/// Whether a van request is a resend of something this system already holds, which the cut-off lets
/// through to be answered as it always was.
/// </summary>
/// <remarks>
/// A van handset hangs up after 30 seconds and sends the same request again under the same reference. A
/// sale started at 16:59 and resent at 17:01 has to reach the code that finds its reservation and hands
/// back its receipt; refusing it would leave the rep holding a sale the office already has.
/// </remarks>
public static class TradingCutoffResends
{
    /// <summary>A van sale whose reservation, receipt row or queue entry already exists.</summary>
    public static async Task<bool> IsKnownVanSaleAsync(
        ApplicationDbContext db, string? vanOrder, CancellationToken cancellationToken)
    {
        var reference = vanOrder?.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        return await db.StockReservations.AsNoTracking().AnyAsync(row => row.ExternalReferenceId == reference, cancellationToken)
            || await db.DesktopSales.AsNoTracking().AnyAsync(row => row.ExternalReferenceId == reference, cancellationToken)
            || await db.InvoiceQueue.AsNoTracking().AnyAsync(row => row.ExternalReference == reference, cancellationToken);
    }

    /// <summary>A van sales order already captured under this van order.</summary>
    public static async Task<bool> IsKnownSalesOrderAsync(
        ApplicationDbContext db, string? vanOrder, CancellationToken cancellationToken)
    {
        var reference = vanOrder?.Trim();
        return !string.IsNullOrEmpty(reference)
            && await db.SalesOrders.AsNoTracking().AnyAsync(order => order.ClientRequestId == reference, cancellationToken);
    }

    /// <summary>A sales order whose conversion to an invoice has already been queued.</summary>
    public static Task<bool> IsKnownConversionAsync(
        ApplicationDbContext db, int salesOrderId, CancellationToken cancellationToken) =>
        db.InvoiceQueue.AsNoTracking().AnyAsync(row => row.SalesOrderId == salesOrderId, cancellationToken);
}
