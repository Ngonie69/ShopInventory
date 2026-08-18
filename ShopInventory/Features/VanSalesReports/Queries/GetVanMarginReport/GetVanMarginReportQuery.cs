using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanMarginReport;

/// <summary>
/// What sold off the vans, per item and per van, and how much of it SAP is in a position to cost.
/// </summary>
/// <remarks>
/// This is the reporting plan's A10 — gross margin by SKU and route — with the cost half not yet
/// connected. Every margin field on every record below is nullable and is null today, and the report
/// says so on its own face rather than rendering a zero that reads as selling at cost.
///
/// <b>Why it ships without the half it is named for.</b> No item cost is read anywhere in either
/// project; the profit report's stock value is a hard-coded zero. The cost has to come from SAP, and
/// the column that carries it on an invoice line could not be established from this repository: the
/// codebase has proven exactly six INV1 columns against the live instance — <c>DocEntry</c>,
/// <c>ItemCode</c>, <c>LineNum</c>, <c>LineTotal</c>, <c>Quantity</c> and <c>WhsCode</c> — and not
/// one of them is a cost. The service-layer metadata names <c>GrossBuyPrice</c> and
/// <c>GrossProfit</c> on the OData document line, but those are OData property names and the SQL
/// path needs the HANA column, which is a different string. Guessing it and shipping a margin
/// column filled from the wrong field is the failure this suite has spent four phases avoiding.
///
/// <b>What it answers instead, which is worth having on its own.</b> A margin figure can only exist
/// for a sale SAP knows about, and a great many van sales are not. The offline half is held locally
/// until a posting job drains it, and that job is switched off — so the costable share of van
/// revenue is well under all of it, and nothing anywhere said so at item grain. This report is that
/// denominator: what sold, and what share of it SAP could put a cost against if asked.
///
/// When the cost column is settled, this report gains columns rather than changing shape. The join
/// is (warehouse, item), and the line-grain warehouse is populated on both halves of the union —
/// which is why the report keys on it rather than on the van's business partner, whose code is the
/// van's own account on every sale and identifies nothing about the goods.
///
/// There is deliberately no "lines with no warehouse" figure. Both line entities mark the column
/// required, and the offline ingest refuses an entire batch from a rep with no assigned warehouse
/// rather than accepting one — so the count could never be anything but zero, and a counter that
/// cannot move reads as a check that passed rather than one that cannot fail.
/// </remarks>
public sealed record GetVanMarginReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? UserId = null,
    string? WarehouseCode = null
) : IRequest<ErrorOr<VanMarginReportResult>>;

public sealed record VanMarginReportResult(
    DateTime FromDate,
    DateTime ToDate,
    VanMarginSummaryResult Summary,
    List<VanMarginItemResult> Items,
    List<VanMarginVanResult> Vans,
    VanMarginQualityResult Quality
);

// ── Summary ─────────────────────────────────────────────────────────────────────

public sealed record VanMarginSummaryResult(
    int ItemCount,
    int VanCount,
    int LineCount,
    int PostedLineCount,
    List<VanSalesLineMoneyResult> RevenueByCurrency,
    List<VanSalesLineMoneyResult> CostableRevenueByCurrency,
    List<VanSalesQuantityResult> QuantitiesByUoM)
{
    /// <summary>
    /// The share of lines whose sale reached SAP, and which a cost could therefore be found for.
    /// Null when nothing sold — a period with no lines has no costable share, and 0% would read as
    /// an estate whose posting has completely failed rather than one that had a quiet week.
    /// </summary>
    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;

    /// <summary>
    /// Margin is not computed. Stated as a field rather than left to the reader to infer from a
    /// column of dashes, because a reader who infers it wrongly infers that the vans sell at cost.
    /// </summary>
    public bool MarginAvailable => false;
}

// ── Per item ────────────────────────────────────────────────────────────────────

/// <summary>
/// One item across every van in the period.
/// </summary>
/// <remarks>
/// Quantity is per unit of measure and never totalled across units. Van lines carry no unit at all —
/// neither ingest path writes one — so in practice this is a single "not recorded" bucket, and that
/// is honest rather than broken.
/// </remarks>
public sealed record VanMarginItemResult(
    string ItemCode,
    string? ItemDescription,
    int LineCount,
    int PostedLineCount,
    int VanCount,
    List<VanSalesLineMoneyResult> RevenueByCurrency,
    List<VanSalesLineMoneyResult> CostableRevenueByCurrency,
    List<VanSalesQuantityResult> QuantitiesByUoM,
    decimal? UnitCost,
    List<VanSalesLineMoneyResult>? CostByCurrency)
{
    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;

    /// <summary>
    /// Gross margin, once a cost is joined. Null today for every item, and null is the whole point:
    /// a zero here would report an item sold at exactly cost, which is a finding rather than an
    /// absence.
    /// </summary>
    public List<VanSalesLineMoneyResult>? MarginByCurrency => null;
}

// ── Per van ─────────────────────────────────────────────────────────────────────

/// <summary>
/// One van's warehouse across every item in the period.
/// </summary>
/// <remarks>
/// Keyed on the warehouse rather than on the business partner. <c>CardCode</c> on a van sale is the
/// van's own SAP account, which is the same on every sale a route-customer van makes and says
/// nothing about the goods; the warehouse is what an invoice line carries and is therefore the only
/// key a cost can be joined on.
/// </remarks>
public sealed record VanMarginVanResult(
    string WarehouseCode,
    string? Username,
    string? FullName,
    int ItemCount,
    int LineCount,
    int PostedLineCount,
    List<VanSalesLineMoneyResult> RevenueByCurrency,
    List<VanSalesLineMoneyResult> CostableRevenueByCurrency)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(FullName)
            ? string.IsNullOrWhiteSpace(Username) ? WarehouseCode : Username
            : FullName;

    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;
}

// ── Quality ─────────────────────────────────────────────────────────────────────

public sealed record VanMarginQualityResult(
    int LineCount,
    int PostedLineCount,
    int ItemsWithNoDescription,
    bool PostingJobEnabled)
{
    public int UnpostedLineCount => LineCount - PostedLineCount;

    /// <summary>
    /// Never clean, and deliberately so. The report is named for a figure it does not yet carry, and
    /// a page that could report itself complete while missing its own subject would be lying by
    /// omission.
    /// </summary>
    public bool IsClean => false;

    public IEnumerable<string> Caveats
    {
        get
        {
            yield return
                "Margin is not computed. No item cost is read anywhere in this system, and the SAP "
                + "column that carries it on an invoice line has not been established against this "
                + "company's data — so a cost column here would be a guess. Everything below is "
                + "revenue and quantity only.";

            if (!PostingJobEnabled)
            {
                yield return
                    "The van sales posting job is switched off in this environment, so offline van "
                    + "sales never reach SAP however long they wait. They can never be costed while "
                    + "that is true, and they are the larger half of most vans' trading.";
            }

            if (UnpostedLineCount > 0)
            {
                yield return
                    $"{UnpostedLineCount:N0} of {LineCount:N0} line(s) are on a sale that has not "
                    + "reached SAP. A cost can only ever be found for a line SAP knows about, so that "
                    + "share of revenue is outside any margin figure this report could eventually "
                    + "produce.";
            }

            if (ItemsWithNoDescription > 0)
            {
                yield return
                    $"{ItemsWithNoDescription:N0} item(s) sold under a code with no description on "
                    + "any line. They are reported by code alone rather than being dropped.";
            }
        }
    }
}
