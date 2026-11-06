using ShopInventory.Models;

namespace ShopInventory.Common.Sales;

/// <summary>
/// A SAP marketing document's amounts in the document's own currency: sales orders, quotations,
/// purchase orders, purchase invoices, goods receipt POs and purchase quotations.
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
    /// <summary>Whether SAP holds the document in a currency other than the company's local one.</summary>
    public static bool IsForeignCurrency(ISapDocumentAmounts document) =>
        document.DocTotalFc is { } foreignTotal && foreignTotal != 0m;

    /// <summary>The document total, tax included, in the document's currency. Null when SAP sent no total.</summary>
    public static decimal? DocTotal(ISapDocumentAmounts document) =>
        IsForeignCurrency(document) ? document.DocTotalFc : document.DocTotal;

    /// <summary>The document's tax in its own currency. Null when SAP sent none.</summary>
    public static decimal? VatSum(ISapDocumentAmounts document) =>
        IsForeignCurrency(document)
            ? document.VatSumFc ?? ToDocumentCurrency(document, document.VatSum)
            : document.VatSum;

    /// <summary>The header discount in the document's own currency. Null when SAP sent none.</summary>
    public static decimal? TotalDiscount(ISapDocumentAmounts document) =>
        IsForeignCurrency(document)
            ? document.TotalDiscountFC ?? ToDocumentCurrency(document, document.TotalDiscount)
            : document.TotalDiscount;

    /// <summary>A line's total in the document's currency. Null when SAP sent none.</summary>
    public static decimal? LineTotal(ISapDocumentAmounts document, ISapDocumentLineAmounts line) =>
        IsForeignCurrency(document)
            ? line.RowTotalFC ?? ToDocumentCurrency(document, line.LineTotal)
            : line.LineTotal;

    /// <summary>
    /// Converts a local-currency amount at the document's own ratio of its two totals, for a read
    /// that left the foreign-currency field out of its <c>$select</c>. A local-currency document's
    /// amount comes back as it went in.
    /// </summary>
    public static decimal? ToDocumentCurrency(ISapDocumentAmounts document, decimal? localAmount)
    {
        if (localAmount is not { } amount)
            return null;

        if (!IsForeignCurrency(document))
            return amount;

        if (document.DocTotal is not { } localTotal || localTotal == 0m)
            return document.DocRate is { } rate && rate > 0m ? Math.Round(amount * rate, 2) : null;

        return Math.Round(amount * document.DocTotalFc.GetValueOrDefault() / localTotal, 2);
    }
}
