using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components;

/// <summary>
/// Which business partners /reports/order-fulfillment is reporting on, and the report narrowed to
/// them.
/// </summary>
/// <remarks>
/// <para>
/// The narrowing happens here rather than in the statement SAP runs. The four report statements are
/// constant text with the date range bound, so one SAP query object serves every range asked for;
/// a partner set is a different length every time it is asked for, so pushing it into the text
/// would leave a permanent OUQR row per distinct selection. A group and a code range could be bound
/// — they are single values — but the report a reader is looking at is already in hand, and
/// narrowing what is in hand costs no SAP read at all.
/// </para>
/// <para>
/// What that buys has to be paid for in arithmetic: the six figures across the top of the page, the
/// per-customer breakdown and the daily series are all totals of the whole loaded window, so a
/// narrowed report has to recompute every one of them or the headline would describe a set the
/// table below it is not showing. <see cref="Narrow"/> is that recomputation, and it follows the
/// API's own — see <c>BuildOrderFulfillmentOrderDetails</c> and <c>BuildInvoiceByCustomer</c> in
/// ShopInventory/Services/ReportService.cs — so a selection covering every partner produces the
/// figures the API sent.
/// </para>
/// </remarks>
public static class OrderFulfillmentScope
{
    /// <summary>
    /// The card codes a group and a selection name together, or null for "every partner".
    /// </summary>
    /// <remarks>
    /// The selection is the answer wherever there is one: the picker only ever offers the chosen
    /// group's partners, so anything selected was selected out of it, and a later change of group
    /// must not silently drop a partner already named. A group on its own means the whole group,
    /// which is what makes the dropdown a criterion in its own right rather than only a way of
    /// finding one partner among thousands.
    /// </remarks>
    public static IReadOnlyCollection<string>? Resolve(
        IReadOnlyList<OrderFulfillmentPartnerPicker.Option> partners,
        string? groupCode,
        IReadOnlyList<string> selected)
    {
        if (selected.Count > 0)
        {
            return selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var group = groupCode?.Trim();

        if (string.IsNullOrEmpty(group))
        {
            return null;
        }

        return partners
            .Where(partner => string.Equals(partner.Group, group, StringComparison.OrdinalIgnoreCase))
            .Select(partner => partner.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="report"/> holding only the orders of <paramref name="cardCodes"/>, with
    /// every total recomputed for that set.
    /// </summary>
    /// <remarks>
    /// Null is every partner; an empty set is no partner, which is a group the cache holds nobody
    /// for rather than a filter to ignore. A set that excludes nothing returns the report unchanged
    /// rather than a rebuilt copy of it, so an unnarrowed page shows the API's own figures rather
    /// than this file's reading of them.
    /// </remarks>
    public static OrderFulfillmentReport Narrow(
        OrderFulfillmentReport report,
        IReadOnlyCollection<string>? cardCodes)
    {
        if (cardCodes is null)
        {
            return report;
        }

        var wanted = cardCodes as HashSet<string> ?? cardCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orders = report.Orders.Where(order => wanted.Contains(order.CardCode)).ToList();

        return orders.Count == report.Orders.Count ? report : Rebuild(report, orders);
    }

    private static OrderFulfillmentReport Rebuild(OrderFulfillmentReport source, List<OrderFulfillmentItem> orders)
    {
        // Every order in the window reaches the page non-cancelled — the statements filter
        // CANCELED = 'N' — but the API still counts a cancelled one out of its totals, so this does
        // too rather than assuming the filter will always be there.
        var live = orders.Where(order => !IsCancelled(order.Status)).ToList();

        decimal quantityOrdered = 0, quantityDelivered = 0;
        decimal valueUsd = 0, valueZig = 0;
        decimal deliveredUsd = 0, deliveredZig = 0;
        decimal pendingUsd = 0, pendingZig = 0;
        var usdOrders = 0;
        var fullyDelivered = 0;
        var partiallyDelivered = 0;
        var undelivered = 0;

        foreach (var order in live)
        {
            var usd = IsUsd(order.DocCurrency);
            var zig = IsZig(order.DocCurrency);

            if (usd)
            {
                valueUsd += order.OrderTotal;
                usdOrders++;
            }
            else if (zig)
            {
                valueZig += order.OrderTotal;
            }

            foreach (var line in order.Lines)
            {
                quantityOrdered += line.QuantityOrdered;
                quantityDelivered += line.QuantityDelivered;

                if (line.QuantityPending <= 0)
                {
                    fullyDelivered++;
                }
                else if (line.QuantityDelivered > 0)
                {
                    partiallyDelivered++;
                }
                else
                {
                    undelivered++;
                }

                var pending = PendingLineValue(line);

                if (usd)
                {
                    deliveredUsd += line.InvoicedValue;
                    pendingUsd += pending;
                }
                else if (zig)
                {
                    deliveredZig += line.InvoicedValue;
                    pendingZig += pending;
                }
            }
        }

        var closed = live.Count(order => IsClosed(order.Status));
        var open = live.Count - closed;

        // The API reads the invoice rate off quantity where it has line detail, and falls back to
        // the share of orders closed where it has none. Both are kept: a window whose every line
        // carries a zero quantity would otherwise report 0% rather than the closure it does know.
        var rate = quantityOrdered > 0
            ? quantityDelivered / quantityOrdered * 100
            : live.Count > 0 ? (decimal)closed / live.Count * 100 : 0;

        return new OrderFulfillmentReport
        {
            FromDate = source.FromDate,
            ToDate = source.ToDate,
            TotalOrders = orders.Count,
            OpenOrders = open,
            ClosedOrders = closed,
            CancelledOrders = orders.Count - live.Count,
            FulfillmentRatePercent = Math.Round(rate, 1),
            TotalOrderValueUSD = valueUsd,
            TotalOrderValueZIG = valueZig,
            TotalDeliveredValueUSD = Math.Round(deliveredUsd, 2),
            TotalDeliveredValueZIG = Math.Round(deliveredZig, 2),
            TotalPendingValueUSD = Math.Round(pendingUsd, 2),
            TotalPendingValueZIG = Math.Round(pendingZig, 2),
            AverageOrderValueUSD = usdOrders > 0 ? Math.Round(valueUsd / usdOrders, 2) : 0,
            TotalLineItems = orders.Sum(order => order.Lines.Count),
            FullyDeliveredLines = fullyDelivered,
            PartiallyDeliveredLines = partiallyDelivered,
            UndeliveredLines = undelivered,
            Orders = orders,
            FulfillmentByCustomer = BuildByCustomer(live),
            DailyFulfillment = BuildDaily(live)
        };
    }

    private static List<FulfillmentByCustomer> BuildByCustomer(IEnumerable<OrderFulfillmentItem> live) =>
        live
            .GroupBy(order => new { order.CardCode, order.CardName })
            .Select(group =>
            {
                var ordered = group.Sum(order => order.TotalQuantityOrdered);
                var invoiced = group.Sum(order => order.TotalQuantityDelivered);

                return new FulfillmentByCustomer
                {
                    CardCode = group.Key.CardCode,
                    CardName = group.Key.CardName,
                    TotalOrders = group.Count(),
                    // "Open" here is the API's: an order with quantity still to invoice, which is
                    // not the same question as the document's own status.
                    OpenOrders = group.Count(order => order.TotalQuantityPending > 0),
                    ClosedOrders = group.Count(order => order.TotalQuantityPending <= 0),
                    TotalOrderValue = group.Sum(order => order.OrderTotal),
                    FulfillmentRatePercent = ordered > 0 ? Math.Round(invoiced / ordered * 100, 1) : 0,
                    TotalPendingValue = group.Sum(order => order.Lines.Sum(PendingLineValue))
                };
            })
            .OrderByDescending(customer => customer.TotalOrders)
            .ToList();

    private static List<DailyFulfillment> BuildDaily(IEnumerable<OrderFulfillmentItem> live) =>
        live
            .Where(order => order.OrderDate != default && order.OrderDate != DateTime.MinValue)
            .GroupBy(order => order.OrderDate.Date)
            .Select(group => new DailyFulfillment
            {
                Date = group.Key,
                OrdersPlaced = group.Count(),
                OrdersClosed = group.Count(order => IsClosed(order.Status)),
                OrderValueUSD = group.Where(order => IsUsd(order.DocCurrency)).Sum(order => order.OrderTotal),
                QuantityOrdered = group.Sum(order => order.TotalQuantityOrdered),
                QuantityDelivered = group.Sum(order => order.TotalQuantityDelivered)
            })
            .OrderBy(day => day.Date)
            .ToList();

    /// <summary>The share of a line's value still to be invoiced, prorated on quantity.</summary>
    private static decimal PendingLineValue(OrderLineDetail line) =>
        line.QuantityOrdered > 0
            ? line.LineTotal * line.QuantityPending / line.QuantityOrdered
            : 0;

    private static bool IsClosed(string? status) =>
        string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase);

    private static bool IsCancelled(string? status) =>
        string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase);

    /// <summary>An unset currency counted as USD in the SQL predicates this replaced, and still does.</summary>
    private static bool IsUsd(string? currency) =>
        string.IsNullOrEmpty(currency)
        || string.Equals(currency, "USD", StringComparison.Ordinal)
        || string.Equals(currency, "$", StringComparison.Ordinal);

    private static bool IsZig(string? currency) =>
        string.Equals(currency, "ZIG", StringComparison.OrdinalIgnoreCase);
}
