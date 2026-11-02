namespace ShopInventory.Models;

/// <summary>
/// The two sets of amounts SAP keeps on a marketing document header (sales order, quotation,
/// purchase order, purchase invoice, goods receipt PO, purchase quotation).
/// </summary>
/// <remarks>
/// The plain fields are in the company's local currency (USD) whatever the document's currency.
/// The <c>Fc</c>/<c>FC</c> fields are in the document's own currency and are zero on a USD document.
/// Read them through <see cref="Common.Sales.SapDocumentAmounts"/>, which picks the right set.
/// </remarks>
public interface ISapDocumentAmounts
{
    /// <summary>Total, tax included, in local currency (USD).</summary>
    decimal? DocTotal { get; }

    /// <summary>Total in the document's currency; zero on a local-currency document.</summary>
    decimal? DocTotalFc { get; }

    /// <summary>Tax in local currency (USD).</summary>
    decimal? VatSum { get; }

    /// <summary>Tax in the document's currency.</summary>
    decimal? VatSumFc { get; }

    /// <summary>Header discount in local currency (USD).</summary>
    decimal? TotalDiscount { get; }

    /// <summary>Header discount in the document's currency.</summary>
    decimal? TotalDiscountFC { get; }

    /// <summary>Document currency per unit of local currency.</summary>
    decimal? DocRate { get; }
}

/// <summary>A marketing document line's total in both of SAP's currencies.</summary>
public interface ISapDocumentLineAmounts
{
    /// <summary>Line total in local currency (USD).</summary>
    decimal? LineTotal { get; }

    /// <summary>Line total in the document's currency; zero on a local-currency document.</summary>
    decimal? RowTotalFC { get; }
}
