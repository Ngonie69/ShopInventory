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
/// <para>
/// <b>And the money.</b> SAP rounds each line's net total to the cent, so a line cut in two is rounded twice.
/// Ten units at 3.4632 are 34.63 on one line and 17.32 + 17.32 = 34.64 on two, and the VAT on the extra cent
/// adds another. The sale's receipt was signed before SAP was asked, from the uncut line, so every split
/// invoice came out a cent or three away from its receipt: in November 2026 SAP invoice 814591 charged
/// 48.71 against a receipt of 48.69. See <see cref="BalancePortions"/>.
/// </para>
/// </remarks>
public static class QueuedVanInvoiceLines
{
    /// <summary>The scale SAP keeps a unit price to, and the finest step a portion's price can move.</summary>
    private const int PriceDecimals = 6;

    private const decimal PriceStep = 0.000001m;

    /// <summary>How many price steps either side of the first guess to try before giving up.</summary>
    private const int PriceSearchSteps = 50;

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
            var portionsFrom = based.Count;

            foreach (var orderLine in ConsolidatedInvoiceLines.OpenLinesFor(order, line.ItemCode ?? string.Empty))
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

            BalancePortions(line, based.GetRange(portionsFrom, based.Count - portionsFrom));
        }

        if (anyLinked)
        {
            lines.Clear();
            lines.AddRange(based);
        }

        return anyLinked;
    }

    /// <summary>
    /// Re-prices the largest portion of a cut line so the portions' net totals add up to the uncut line's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target is the uncut line's net total as the sale was totalled when its receipt was signed:
    /// quantity x price x (1 - discount), rounded once (<c>VanSaleFiscalFirstPoster.BuildSaleAsync</c>). With
    /// the net lines agreeing, SAP's VAT agrees too, because SAP rounds VAT once per rate over the whole
    /// document, as <c>TaxSettings.VatOnBasket</c> does; so the invoice's total is the receipt's.
    /// </para>
    /// <para>
    /// One portion's unit price moves by a fraction of a cent. That is the same trade the fiscalisation
    /// console makes when it fits a receipt's lines to a SAP total (<c>SapReceiptMapper</c>), and SAP keeps
    /// the price to six places, so the portion still prints at the price the customer paid to the cent.
    /// </para>
    /// <para>
    /// When no six-place price lands on the cent — a quantity in the tens of thousands — the portions are left
    /// as they were. The post goes ahead, and <c>FiscalReceiptAmountCheck</c> reports the difference.
    /// </para>
    /// </remarks>
    internal static void BalancePortions(CreateInvoiceLineRequest line, IReadOnlyList<CreateInvoiceLineRequest> portions)
    {
        if (portions.Count < 2 || line.UnitPrice is not decimal unitPrice || unitPrice <= 0m)
        {
            return;
        }

        var discount = line.DiscountPercent ?? 0m;
        var target = RoundMoney(line.Quantity * unitPrice * (1 - discount / 100m));

        var adjusted = portions
            .Select((portion, index) => (portion, index))
            .OrderByDescending(entry => entry.portion.Quantity)
            .ThenByDescending(entry => entry.index)
            .First()
            .portion;

        var others = portions.Where(portion => !ReferenceEquals(portion, adjusted)).Sum(SapNetTotal);
        var wanted = target - others;

        if (SapNetTotal(adjusted) == wanted || wanted <= 0m || adjusted.Quantity <= 0m || discount >= 100m)
        {
            return;
        }

        var guess = Math.Round(wanted / (adjusted.Quantity * (1 - discount / 100m)), PriceDecimals, MidpointRounding.AwayFromZero);

        for (var step = 0; step <= PriceSearchSteps; step++)
        {
            foreach (var candidate in step == 0 ? [guess] : new[] { guess - step * PriceStep, guess + step * PriceStep })
            {
                if (candidate > 0m && NetTotal(adjusted.Quantity, candidate, discount) == wanted)
                {
                    adjusted.UnitPrice = candidate;
                    return;
                }
            }
        }
    }

    /// <summary>A line's net total as SAP works it out: the discounted price to six places, times quantity, to the cent.</summary>
    internal static decimal SapNetTotal(CreateInvoiceLineRequest line) =>
        NetTotal(line.Quantity, line.UnitPrice ?? 0m, line.DiscountPercent ?? 0m);

    private static decimal NetTotal(decimal quantity, decimal unitPrice, decimal discountPercent) =>
        RoundMoney(quantity * Math.Round(unitPrice * (1 - discountPercent / 100m), PriceDecimals, MidpointRounding.AwayFromZero));

    private static decimal RoundMoney(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

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
