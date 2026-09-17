using ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;
using ShopInventory.Models;

namespace ShopInventory.Features.DesktopIntegration.Commands.PostQueuedVanInvoices;

/// <summary>
/// Bases the lines of one queued van invoice on the sales order it was converted from.
/// </summary>
/// <remarks>
/// <para>
/// The per-sale sibling of <see cref="ConsolidatedInvoiceLines"/>, and it follows the same rules, for the
/// same reasons: only what an order line still has open is linked, because SAP refuses a whole invoice
/// whose line asks for more; the rest stays on an ordinary line exactly as it would have been.
/// </para>
/// <para>
/// <b>What differs is the batches.</b> A consolidated invoice allocates its batches after its lines are
/// built. This one is posted from a reservation, whose batches were chosen when the sale was made and are
/// held against it, so each line arrives already carrying them. Splitting a line in two has to split its
/// batches with it, or SAP is told one line took batches the other line needed.
/// </para>
/// </remarks>
public static class QueuedVanInvoiceLines
{
    /// <summary>
    /// Replaces <paramref name="lines"/> with lines based on <paramref name="order"/> as far as the order
    /// has quantity open.
    /// </summary>
    /// <returns>Whether any line was linked.</returns>
    public static bool BaseOn(List<CreateInvoiceLineRequest> lines, SAPSalesOrder order)
    {
        var remainingOpen = new Dictionary<int, decimal>();
        var based = new List<CreateInvoiceLineRequest>();
        var anyLinked = false;

        foreach (var line in lines)
        {
            var quantityLeft = line.Quantity;
            var batches = new Queue<BatchNumberRequest>((line.BatchNumbers ?? []).Select(Copy));

            foreach (var orderLine in ConsolidatedInvoiceLines.OpenLinesFor(order, line.ItemCode))
            {
                if (quantityLeft <= 0)
                {
                    break;
                }

                if (!remainingOpen.TryGetValue(orderLine.LineNum, out var open))
                {
                    open = orderLine.RemainingOpenQuantity ?? 0m;
                }

                var linked = Math.Min(quantityLeft, open);
                if (linked <= 0)
                {
                    continue;
                }

                remainingOpen[orderLine.LineNum] = open - linked;
                quantityLeft -= linked;
                anyLinked = true;

                based.Add(Portion(line, linked, batches, order.DocEntry, orderLine.LineNum));
            }

            if (quantityLeft > 0)
            {
                based.Add(Portion(line, quantityLeft, batches, null, null));
            }
        }

        if (anyLinked)
        {
            lines.Clear();
            lines.AddRange(based);
        }

        return anyLinked;
    }

    private static CreateInvoiceLineRequest Portion(
        CreateInvoiceLineRequest line,
        decimal quantity,
        Queue<BatchNumberRequest> batches,
        int? baseEntry,
        int? baseLine) => new()
        {
            ItemCode = line.ItemCode,
            Quantity = quantity,
            UnitPrice = line.UnitPrice,
            WarehouseCode = line.WarehouseCode,
            TaxCode = line.TaxCode,
            DiscountPercent = line.DiscountPercent,
            UoMCode = line.UoMCode,
            CostCentreCode = line.CostCentreCode,
            AccountCode = line.AccountCode,
            AutoAllocateBatches = line.AutoAllocateBatches,
            BatchNumbers = line.BatchNumbers is null ? null : Take(batches, quantity),
            BaseType = baseEntry.HasValue ? ConsolidatedInvoiceLines.SalesOrderObjectType : null,
            BaseEntry = baseEntry,
            BaseLine = baseLine
        };

    /// <summary>Takes <paramref name="quantity"/> from the front of the line's batches, splitting one if it must.</summary>
    private static List<BatchNumberRequest> Take(Queue<BatchNumberRequest> batches, decimal quantity)
    {
        var taken = new List<BatchNumberRequest>();

        while (quantity > 0 && batches.Count > 0)
        {
            var batch = batches.Peek();
            var portion = Math.Min(quantity, batch.Quantity);

            var part = Copy(batch);
            part.Quantity = portion;
            taken.Add(part);

            batch.Quantity -= portion;
            quantity -= portion;

            if (batch.Quantity <= 0)
            {
                batches.Dequeue();
            }
        }

        return taken;
    }

    private static BatchNumberRequest Copy(BatchNumberRequest batch) => new()
    {
        BatchNumber = batch.BatchNumber,
        Quantity = batch.Quantity,
        ExpiryDate = batch.ExpiryDate
    };
}
