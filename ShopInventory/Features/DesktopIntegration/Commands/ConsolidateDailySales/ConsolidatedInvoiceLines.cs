using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;

/// <summary>
/// Builds a consolidated invoice's lines from the day's sales, basing the part of each sale that was
/// converted from a sales order on that order's SAP document.
/// </summary>
/// <remarks>
/// A line based on an order (<c>BaseType</c> 17) is how SAP learns the order was invoiced: the order
/// line's open quantity falls and the order closes once nothing is left. Without it the order stays
/// open in SAP forever, beside an invoice for the same goods.
///
/// <para>
/// Only what the order still has open is linked. A rep can deliver more than was ordered, or a second
/// conversion can land on an order the first one already used up, and SAP answers a line that asks for
/// more than is open by refusing the whole invoice, which would hold up every other sale in the
/// customer's day. So the quantity is split: up to the open quantity is based on the order, and the
/// rest goes on an ordinary line exactly as it would have before.
/// </para>
///
/// <para>
/// Lines are merged as they always were, by item, warehouse, price, tax code, discount and cost centre.
/// The base line is part of the key, so linked quantities merge only with other quantities against the
/// same order line, and unlinked lines merge exactly as they did before this existed.
/// </para>
/// </remarks>
public static class ConsolidatedInvoiceLines
{
    /// <summary>SAP's object type for a sales order.</summary>
    public const int SalesOrderObjectType = 17;

    public static List<CreateInvoiceLineRequest> Build(
        IEnumerable<DesktopSaleEntity> sales,
        IReadOnlyDictionary<DesktopSaleEntity, SAPSalesOrder> baseOrders)
    {
        // What each order line still has open, drawn down as sales are linked to it. Shared across the
        // group, because two conversions of one order can be consolidated on the same day.
        var remainingOpen = new Dictionary<(int DocEntry, int LineNum), decimal>();
        var portions = new List<Portion>();

        foreach (var sale in sales)
        {
            baseOrders.TryGetValue(sale, out var baseOrder);

            foreach (var line in sale.Lines)
            {
                var quantityLeft = line.Quantity;

                if (baseOrder is not null)
                {
                    foreach (var orderLine in OpenLinesFor(baseOrder, line.ItemCode))
                    {
                        if (quantityLeft <= 0)
                        {
                            break;
                        }

                        var key = (baseOrder.DocEntry, orderLine.LineNum);
                        if (!remainingOpen.TryGetValue(key, out var open))
                        {
                            open = orderLine.RemainingOpenQuantity ?? 0m;
                        }

                        var linked = Math.Min(quantityLeft, open);
                        if (linked <= 0)
                        {
                            continue;
                        }

                        remainingOpen[key] = open - linked;
                        quantityLeft -= linked;
                        portions.Add(new Portion(line, linked, baseOrder.DocEntry, orderLine.LineNum));
                    }
                }

                if (quantityLeft > 0)
                {
                    portions.Add(new Portion(line, quantityLeft, null, null));
                }
            }
        }

        return portions
            .GroupBy(p => new
            {
                p.Line.ItemCode,
                p.Line.WarehouseCode,
                p.Line.UnitPrice,
                p.Line.TaxCode,
                p.Line.DiscountPercent,
                p.Line.CostCentreCode,
                p.BaseEntry,
                p.BaseLine
            })
            .Select(g => new CreateInvoiceLineRequest
            {
                ItemCode = g.Key.ItemCode,
                Quantity = g.Sum(p => p.Quantity),
                UnitPrice = g.Key.UnitPrice,
                WarehouseCode = g.Key.WarehouseCode,
                TaxCode = g.Key.TaxCode,
                DiscountPercent = g.Key.DiscountPercent,
                CostCentreCode = g.Key.CostCentreCode,
                AutoAllocateBatches = true,
                BaseType = g.Key.BaseEntry.HasValue ? SalesOrderObjectType : null,
                BaseEntry = g.Key.BaseEntry,
                BaseLine = g.Key.BaseLine
            })
            .ToList();
    }

    /// <summary>
    /// Takes the base document off every line, for a repost SAP will accept without the link.
    /// </summary>
    /// <returns>Whether any line was linked, and so whether a repost could differ at all.</returns>
    public static bool Unlink(IEnumerable<CreateInvoiceLineRequest> lines)
    {
        var anyLinked = false;

        foreach (var line in lines)
        {
            anyLinked |= line.BaseEntry.HasValue;
            line.BaseType = null;
            line.BaseEntry = null;
            line.BaseLine = null;
        }

        return anyLinked;
    }

    /// <summary>
    /// Why an invoice for <paramref name="cardCode"/> cannot be based on <paramref name="order"/>, or null
    /// if it can.
    /// </summary>
    /// <remarks>
    /// Shared with the per-sale posting of queued van invoices, so the two routes that link an invoice to
    /// its order agree about which orders may be linked.
    /// </remarks>
    public static string? WhyNotBaseable(SAPSalesOrder? order, string cardCode) => order switch
    {
        null => "SAP did not return it",
        _ when !string.Equals(order.CardCode?.Trim(), cardCode.Trim(), StringComparison.OrdinalIgnoreCase)
            => $"it belongs to {order.CardCode}",
        _ when string.Equals(order.Cancelled, "tYES", StringComparison.OrdinalIgnoreCase) => "it is cancelled",
        _ when !string.Equals(order.DocumentStatus, "bost_Open", StringComparison.OrdinalIgnoreCase) => "it is closed",
        _ => null
    };

    internal static IEnumerable<SAPSalesOrderLine> OpenLinesFor(SAPSalesOrder order, string itemCode) =>
        (order.DocumentLines ?? [])
            .Where(orderLine =>
                string.Equals(orderLine.ItemCode?.Trim(), itemCode.Trim(), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(orderLine.LineStatus, "bost_Close", StringComparison.OrdinalIgnoreCase)
                && orderLine.RemainingOpenQuantity > 0)
            .OrderBy(orderLine => orderLine.LineNum);

    private sealed record Portion(DesktopSaleLineEntity Line, decimal Quantity, int? BaseEntry, int? BaseLine);
}
