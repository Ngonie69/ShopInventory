using ShopInventory.Models;

namespace ShopInventory.Common.Sales;

/// <summary>
/// A SAP sales order's amounts in the order's own currency.
/// </summary>
/// <remarks>
/// SAP keeps two sets of figures on every document. <c>DocTotal</c>, <c>VatSum</c>,
/// <c>TotalDiscount</c> and each line's <c>LineTotal</c> are in the company's local currency (USD).
/// The same figures in the document's own currency are <c>DocTotalFc</c>, <c>VatSumFc</c>,
/// <c>TotalDiscountFC</c> and <c>RowTotalFC</c>, and SAP fills them only when that currency is a
/// foreign one; on a USD document they are zero.
///
/// <para>
/// Reading the local set for a ZiG order shows its USD value under a ZiG label: SO-20261102-0063
/// (TMP191, SAP 86228) is ZiG 229,172.44 in SAP and read back as "ZiG 7,639.08", its value
/// divided by the rate of 30.
/// </para>
/// </remarks>
public static class SapDocumentAmounts
{
    /// <summary>Whether SAP holds the order in a currency other than the company's local one.</summary>
    public static bool IsForeignCurrency(SAPSalesOrder order) =>
        order.DocTotalFc is { } foreignTotal && foreignTotal != 0m;

    /// <summary>The order total, tax included, in the order's currency. Null when SAP sent no total.</summary>
    public static decimal? DocTotal(SAPSalesOrder order) =>
        IsForeignCurrency(order) ? order.DocTotalFc : order.DocTotal;

    /// <summary>The order's tax in its own currency. Null when SAP sent none.</summary>
    public static decimal? VatSum(SAPSalesOrder order) =>
        IsForeignCurrency(order)
            ? order.VatSumFc ?? ToDocumentCurrency(order, order.VatSum)
            : order.VatSum;

    /// <summary>The header discount in the order's own currency. Null when SAP sent none.</summary>
    public static decimal? TotalDiscount(SAPSalesOrder order) =>
        IsForeignCurrency(order)
            ? order.TotalDiscountFC ?? ToDocumentCurrency(order, order.TotalDiscount)
            : order.TotalDiscount;

    /// <summary>A line's total in the order's currency. Null when SAP sent none.</summary>
    public static decimal? LineTotal(SAPSalesOrder order, SAPSalesOrderLine line) =>
        IsForeignCurrency(order)
            ? line.RowTotalFC ?? ToDocumentCurrency(order, line.LineTotal)
            : line.LineTotal;

    /// <summary>
    /// Converts a local-currency amount at the document's own ratio of its two totals, for a read
    /// that left the foreign-currency field out of its <c>$select</c>.
    /// </summary>
    private static decimal? ToDocumentCurrency(SAPSalesOrder order, decimal? localAmount)
    {
        if (localAmount is not { } amount)
            return null;

        if (order.DocTotal is not { } localTotal || localTotal == 0m)
            return order.DocRate is { } rate && rate > 0m ? Math.Round(amount * rate, 2) : null;

        return Math.Round(amount * order.DocTotalFc.GetValueOrDefault() / localTotal, 2);
    }
}
