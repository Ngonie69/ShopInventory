using ShopInventory.Web.Components;
using ShopInventory.Web.Models;

using Option = ShopInventory.Web.Components.OrderFulfillmentPartnerPicker.Option;

namespace ShopInventory.Tests;

/// <summary>
/// The partner scope on /reports/order-fulfillment: which partners a group and a selection name,
/// and what the report's figures become once it is narrowed to them.
/// </summary>
/// <remarks>
/// The arithmetic is the point. Narrowing on the page rather than in the statement SAP runs means
/// the six figures across the top, the per-customer breakdown and the daily series are all this
/// code's rather than the API's, and a figure that describes the whole window while the table under
/// it shows one group is worse than no filter at all. Every expected value below is hand-computed
/// from the fixture, not read back out of the implementation.
/// </remarks>
public class OrderFulfillmentScopeTests
{
    private static readonly Option[] Partners =
    [
        new("ABS006", "Abercorn Stores",     Group: "100"),
        new("BP0876", "Bulawayo Provisions", Group: "102"),
        new("BP0877", "Bindura Post",        Group: "102"),
        new("CRA001", "Cash Sale",           Group: null)
    ];

    // Two customers, three orders, in two currencies:
    //
    //   ABS006  SO 1  USD  closed  total 100  one line: 10 ordered, 10 invoiced, value 100/100
    //   ABS006  SO 2  USD  open    total  50  one line: 10 ordered,  4 invoiced, value  50/ 20
    //   BP0876  SO 3  ZiG  open    total 200  one line: 20 ordered,  0 invoiced, value 200/  0
    private static OrderFulfillmentReport BuildReport() => new()
    {
        FromDate = new DateTime(2026, 1, 1),
        ToDate = new DateTime(2026, 1, 31),
        Orders =
        [
            Order("ABS006", 1, "USD", "Closed", 100m, new DateTime(2026, 1, 5),
                Line(ordered: 10m, delivered: 10m, lineTotal: 100m, invoiced: 100m)),
            Order("ABS006", 2, "USD", "Open", 50m, new DateTime(2026, 1, 6),
                Line(ordered: 10m, delivered: 4m, lineTotal: 50m, invoiced: 20m)),
            Order("BP0876", 3, "ZIG", "Open", 200m, new DateTime(2026, 1, 5),
                Line(ordered: 20m, delivered: 0m, lineTotal: 200m, invoiced: 0m))
        ]
    };

    private static OrderFulfillmentItem Order(
        string cardCode, int docNum, string currency, string status, decimal total, DateTime date,
        params OrderLineDetail[] lines) => new()
        {
            DocNum = docNum,
            DocEntry = docNum,
            OrderDate = date,
            CardCode = cardCode,
            CardName = Partners.First(partner => partner.Value == cardCode).Name,
            DocCurrency = currency,
            OrderTotal = total,
            Status = status,
            Lines = lines.ToList(),
            TotalQuantityOrdered = lines.Sum(line => line.QuantityOrdered),
            TotalQuantityDelivered = lines.Sum(line => line.QuantityDelivered),
            TotalQuantityPending = lines.Sum(line => line.QuantityPending)
        };

    private static OrderLineDetail Line(decimal ordered, decimal delivered, decimal lineTotal, decimal invoiced) => new()
    {
        ItemCode = "ITEM",
        QuantityOrdered = ordered,
        QuantityDelivered = delivered,
        QuantityPending = ordered - delivered,
        LineTotal = lineTotal,
        InvoicedValue = invoiced
    };

    // ── Resolve ──────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_is_null_when_nothing_is_chosen()
    {
        // Null is what tells the page to leave the report exactly as the API sent it.
        Assert.Null(OrderFulfillmentScope.Resolve(Partners, string.Empty, []));
    }

    [Fact]
    public void Resolve_takes_the_whole_group_when_only_a_group_is_chosen()
    {
        var codes = OrderFulfillmentScope.Resolve(Partners, "102", []);

        Assert.Equal(
            new[] { "BP0876", "BP0877" },
            codes!.OrderBy(code => code, StringComparer.Ordinal));
    }

    [Fact]
    public void Resolve_takes_the_selection_over_the_group()
    {
        // The picker only ever offers the chosen group's partners, so anything selected was
        // selected out of one. A later change of group must not silently drop it.
        var codes = OrderFulfillmentScope.Resolve(Partners, "102", ["ABS006"]);

        Assert.Equal(new[] { "ABS006" }, codes);
    }

    [Fact]
    public void Resolve_is_empty_for_a_group_nothing_belongs_to()
    {
        // Empty is not null: a group with no member means no orders, not every order.
        var codes = OrderFulfillmentScope.Resolve(Partners, "140", []);

        Assert.NotNull(codes);
        Assert.Empty(codes);
    }

    // ── Narrow ───────────────────────────────────────────────────────────────

    [Fact]
    public void Narrow_leaves_the_report_alone_when_no_partner_is_chosen()
    {
        var report = BuildReport();

        Assert.Same(report, OrderFulfillmentScope.Narrow(report, null));
    }

    [Fact]
    public void Narrow_leaves_the_report_alone_when_the_selection_excludes_nothing()
    {
        // The API's own figures, not this file's reading of them, wherever the two would agree.
        var report = BuildReport();

        Assert.Same(report, OrderFulfillmentScope.Narrow(report, new[] { "ABS006", "BP0876" }));
    }

    [Fact]
    public void Narrow_empties_the_report_for_a_group_nothing_belongs_to()
    {
        var narrowed = OrderFulfillmentScope.Narrow(BuildReport(), Array.Empty<string>());

        Assert.Empty(narrowed.Orders);
        Assert.Equal(0, narrowed.TotalOrders);
        Assert.Equal(0m, narrowed.TotalOrderValueUSD);
    }

    [Fact]
    public void Narrow_recomputes_the_headline_figures_for_the_chosen_partner()
    {
        var narrowed = OrderFulfillmentScope.Narrow(BuildReport(), new[] { "ABS006" });

        Assert.Equal(2, narrowed.TotalOrders);
        Assert.Equal(1, narrowed.ClosedOrders);
        Assert.Equal(1, narrowed.OpenOrders);
        Assert.Equal(0, narrowed.CancelledOrders);

        Assert.Equal(2, narrowed.TotalLineItems);
        Assert.Equal(1, narrowed.FullyDeliveredLines);
        Assert.Equal(1, narrowed.PartiallyDeliveredLines);
        Assert.Equal(0, narrowed.UndeliveredLines);

        // 14 of 20 invoiced.
        Assert.Equal(70.0m, narrowed.FulfillmentRatePercent);

        Assert.Equal(150m, narrowed.TotalOrderValueUSD);
        Assert.Equal(120m, narrowed.TotalDeliveredValueUSD);
        Assert.Equal(30m, narrowed.TotalPendingValueUSD);   // 50 * 6/10 on the open order
        Assert.Equal(75m, narrowed.AverageOrderValueUSD);   // 150 over two USD orders
    }

    [Fact]
    public void Narrow_keeps_the_other_currency_out_of_the_usd_totals()
    {
        // The ZiG order is the only one this partner has, so every USD figure has to be zero —
        // including the average, which divides by a count that must not include it.
        var narrowed = OrderFulfillmentScope.Narrow(BuildReport(), new[] { "BP0876" });

        Assert.Equal(0m, narrowed.TotalOrderValueUSD);
        Assert.Equal(0m, narrowed.AverageOrderValueUSD);
        Assert.Equal(0m, narrowed.TotalPendingValueUSD);

        Assert.Equal(200m, narrowed.TotalOrderValueZIG);
        Assert.Equal(0m, narrowed.TotalDeliveredValueZIG);
        Assert.Equal(200m, narrowed.TotalPendingValueZIG);
        Assert.Equal(1, narrowed.UndeliveredLines);
    }

    [Fact]
    public void Narrow_splits_the_whole_window_between_its_two_partners()
    {
        var whole = BuildReport();
        var first = OrderFulfillmentScope.Narrow(whole, new[] { "ABS006" });
        var second = OrderFulfillmentScope.Narrow(whole, new[] { "BP0876" });

        Assert.Equal(whole.Orders.Count, first.TotalOrders + second.TotalOrders);
        Assert.Equal(
            whole.Orders.Sum(order => order.Lines.Count),
            first.TotalLineItems + second.TotalLineItems);
        Assert.Equal(
            whole.Orders.Sum(order => order.OrderTotal),
            first.TotalOrderValueUSD + first.TotalOrderValueZIG
            + second.TotalOrderValueUSD + second.TotalOrderValueZIG);
    }

    [Fact]
    public void Narrow_rebuilds_the_per_customer_breakdown()
    {
        var narrowed = OrderFulfillmentScope.Narrow(BuildReport(), new[] { "ABS006" });

        var customer = Assert.Single(narrowed.FulfillmentByCustomer);
        Assert.Equal("ABS006", customer.CardCode);
        Assert.Equal(2, customer.TotalOrders);
        Assert.Equal(1, customer.OpenOrders);       // the one with quantity still to invoice
        Assert.Equal(1, customer.ClosedOrders);
        Assert.Equal(150m, customer.TotalOrderValue);
        Assert.Equal(70.0m, customer.FulfillmentRatePercent);
        Assert.Equal(30m, customer.TotalPendingValue);
    }

    [Fact]
    public void Narrow_rebuilds_the_daily_series()
    {
        var narrowed = OrderFulfillmentScope.Narrow(BuildReport(), new[] { "ABS006" });

        Assert.Equal(2, narrowed.DailyFulfillment.Count);

        // The 5th holds this partner's closed order only — the other customer's order that day
        // has to be gone from both the count and the value.
        var fifth = narrowed.DailyFulfillment[0];
        Assert.Equal(new DateTime(2026, 1, 5), fifth.Date);
        Assert.Equal(1, fifth.OrdersPlaced);
        Assert.Equal(1, fifth.OrdersClosed);
        Assert.Equal(100m, fifth.OrderValueUSD);
        Assert.Equal(10m, fifth.QuantityOrdered);
        Assert.Equal(10m, fifth.QuantityDelivered);

        var sixth = narrowed.DailyFulfillment[1];
        Assert.Equal(new DateTime(2026, 1, 6), sixth.Date);
        Assert.Equal(0, sixth.OrdersClosed);
        Assert.Equal(4m, sixth.QuantityDelivered);
    }
}
