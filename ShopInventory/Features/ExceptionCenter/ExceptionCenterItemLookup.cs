using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.ExceptionCenter;

/// <summary>
/// Confirms an item the exception center was asked to act on is really there. Acknowledge and
/// assign both need it, and both write state keyed on the source, so a source is only ever
/// taught to one place.
/// </summary>
public static class ExceptionCenterItemLookup
{
    public static async Task<bool> ExistsAsync(
        ApplicationDbContext context,
        string source,
        string itemKey,
        CancellationToken cancellationToken)
    {
        var normalized = ExceptionCenterSources.Normalize(source);

        if (ExceptionCenterSources.IsGuidKeyed(normalized))
        {
            if (!Guid.TryParse(itemKey, out var id))
            {
                return false;
            }

            return normalized switch
            {
                ExceptionCenterSources.PendingInventoryTransferPost => await context.PendingInventoryTransfers
                    .AsNoTracking()
                    .AnyAsync(item => item.Id == id, cancellationToken),
                ExceptionCenterSources.PendingTransferRequestEditApply => await context.PendingTransferRequestEdits
                    .AsNoTracking()
                    .AnyAsync(item => item.Id == id, cancellationToken),
                _ => false
            };
        }

        if (!int.TryParse(itemKey, out var itemId))
        {
            return false;
        }

        return normalized switch
        {
            ExceptionCenterSources.InvoiceQueue => await context.InvoiceQueue
                .AsNoTracking()
                .AnyAsync(q => q.Id == itemId, cancellationToken),
            ExceptionCenterSources.InventoryTransferQueue => await context.InventoryTransferQueue
                .AsNoTracking()
                .AnyAsync(q => q.Id == itemId, cancellationToken),
            ExceptionCenterSources.MobileOrderPostProcessing => await context.MobileOrderPostProcessingQueue
                .AsNoTracking()
                .AnyAsync(q => q.Id == itemId, cancellationToken),
            ExceptionCenterSources.IncomingPaymentQueue => await context.IncomingPaymentQueue
                .AsNoTracking()
                .AnyAsync(q => q.Id == itemId, cancellationToken),
            ExceptionCenterSources.PaymentCallback => await context.PaymentTransactions
                .AsNoTracking()
                .AnyAsync(q => q.Id == itemId, cancellationToken),
            ExceptionCenterSources.VanSalePosting => await context.DesktopSales
                .AsNoTracking()
                .AnyAsync(q => q.Id == itemId, cancellationToken),
            ExceptionCenterSources.PaymentCallbackRejection or ExceptionCenterSources.CreditNoteFiscalization =>
                await context.ExceptionCenterIncidents
                    .AsNoTracking()
                    .AnyAsync(q => q.Id == itemId && q.Source == normalized, cancellationToken),
            _ => false
        };
    }

    /// <summary>
    /// Finds the stored state for an item, or starts a new one. Both keys are written: the
    /// canonical <see cref="ExceptionCenterItemStateEntity.ItemKey"/>, and the legacy int id for
    /// the sources that have one.
    /// </summary>
    public static async Task<ExceptionCenterItemStateEntity> GetOrCreateStateAsync(
        ApplicationDbContext context,
        string source,
        string itemKey,
        CancellationToken cancellationToken)
    {
        var state = await context.ExceptionCenterItemStates
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Source == source && s.ItemKey == itemKey, cancellationToken);

        if (state != null)
        {
            return state;
        }

        state = new ExceptionCenterItemStateEntity
        {
            Source = source,
            ItemKey = itemKey,
            ItemId = ExceptionCenterSources.ToLegacyItemId(itemKey)
        };

        context.ExceptionCenterItemStates.Add(state);
        return state;
    }
}
