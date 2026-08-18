namespace ShopInventory.Features.VanSalesReports.Queries;

// The money and quantity shapes every van sales report shares.
//
// They live here, in the parent namespace, rather than inside one report's folder, because a second
// declaration of them would be the most damaging duplication available in this area: two money types
// that round or average differently, in two reports a manager reads side by side, with no way to tell
// which is right. There is one definition, and every report gets it by being in a child namespace.
//
// The rule they exist to enforce: there is no scalar money field anywhere on any van sales report,
// and no scalar quantity. Both are always a list. USD and ZiG are different money, and van lines
// carry no unit of measure at all, so a total across either is a number describing nothing. Making
// the caller pick a bucket is the point.

/// <summary>
/// One currency's takings at document grain. <c>Gross</c> is the sum of the document totals, so it
/// includes VAT — offline sales carry a VAT figure and online ones do not, so no net is derivable
/// and none is offered.
/// </summary>
public sealed record VanSalesMoneyResult(
    string Currency,
    int DocumentCount,
    int DropCount,
    decimal Gross)
{
    /// <summary>Null rather than zero: a currency with no documents has no average, it has no data.</summary>
    public decimal? AverageDocumentValue =>
        DocumentCount > 0 ? decimal.Round(Gross / DocumentCount, 2) : null;

    /// <summary>
    /// The drop size. A drop is one shop on one day in one currency, so two invoices written at the
    /// same counter are one drop — which is what a field manager means by the word.
    /// </summary>
    public decimal? AverageDropSize =>
        DropCount > 0 ? decimal.Round(Gross / DropCount, 2) : null;
}

/// <summary>
/// One currency's takings at line grain. Deliberately a different type from
/// <see cref="VanSalesMoneyResult"/>: document totals and line totals are two measures, not one, and
/// a row that mixed them would be reconcilable to nothing.
/// </summary>
public sealed record VanSalesLineMoneyResult(
    string Currency,
    int LineCount,
    decimal Gross);

/// <summary>
/// How much moved, in one unit of measure. <c>UoMCode</c> is null for every van sale written to date
/// — neither ingest path sets it — so expect a single "unit not recorded" bucket and do not read that
/// as an error.
/// </summary>
public sealed record VanSalesQuantityResult(
    string? UoMCode,
    decimal Quantity,
    int LineCount);
