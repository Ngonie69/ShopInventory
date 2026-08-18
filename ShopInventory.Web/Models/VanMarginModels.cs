namespace ShopInventory.Web.Models;

/// <summary>
/// The local half of the margin report, mirroring the API's <c>VanMarginReportResult</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored like every other API DTO here, so nullability has to match exactly: a property
/// declared non-nullable against a value the API can send as null makes System.Text.Json throw
/// inside <c>GetFromJsonAsync</c>, the service's catch turns it into null, and the page reports "no
/// data".
///
/// On this report the nulls carry more weight than usual, because the report is named for a figure
/// it does not yet have. <see cref="VanMarginItem.UnitCost"/>,
/// <see cref="VanMarginItem.CostByCurrency"/> and <see cref="VanMarginItem.MarginByCurrency"/> are
/// null on every row and must stay null. A zero in any of them would report an item sold at exactly
/// cost — a finding rather than an absence, and indistinguishable from the real thing once a cost
/// source is connected.
/// </remarks>
public class VanMarginReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public VanMarginSummary Summary { get; set; } = new();
    public List<VanMarginItem> Items { get; set; } = [];
    public List<VanMarginVan> Vans { get; set; } = [];
    public VanMarginQuality Quality { get; set; } = new();
}

// ── Summary ─────────────────────────────────────────────────────────────────────

public class VanMarginSummary
{
    public int ItemCount { get; set; }
    public int VanCount { get; set; }
    public int LineCount { get; set; }
    public int PostedLineCount { get; set; }
    public List<VanSalesLineMoney> RevenueByCurrency { get; set; } = [];
    public List<VanSalesLineMoney> CostableRevenueByCurrency { get; set; } = [];
    public List<VanSalesQuantity> QuantitiesByUoM { get; set; } = [];

    /// <summary>
    /// The share of lines whose sale reached SAP, and which a cost could therefore be found for.
    /// Null when nothing sold — a period with no lines has no costable share, and 0% would read as
    /// an estate whose posting has completely failed rather than one that had a quiet week.
    /// </summary>
    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;

    /// <summary>
    /// Margin is not computed. A field rather than something a reader infers from a column of
    /// dashes, because a reader who infers it wrongly infers that the vans sell at cost.
    /// </summary>
    public bool MarginAvailable => false;
}

// ── Per item ────────────────────────────────────────────────────────────────────

public class VanMarginItem
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public int LineCount { get; set; }
    public int PostedLineCount { get; set; }
    public int VanCount { get; set; }
    public List<VanSalesLineMoney> RevenueByCurrency { get; set; } = [];
    public List<VanSalesLineMoney> CostableRevenueByCurrency { get; set; } = [];
    public List<VanSalesQuantity> QuantitiesByUoM { get; set; } = [];
    public decimal? UnitCost { get; set; }
    public List<VanSalesLineMoney>? CostByCurrency { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;

    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;

    /// <summary>
    /// Gross margin, once a cost is joined. Null today for every item, and null is the whole point.
    /// </summary>
    public List<VanSalesLineMoney>? MarginByCurrency => null;
}

// ── Per van ─────────────────────────────────────────────────────────────────────

public class VanMarginVan
{
    public string WarehouseCode { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? FullName { get; set; }
    public int ItemCount { get; set; }
    public int LineCount { get; set; }
    public int PostedLineCount { get; set; }
    public List<VanSalesLineMoney> RevenueByCurrency { get; set; } = [];
    public List<VanSalesLineMoney> CostableRevenueByCurrency { get; set; } = [];

    public string DisplayName =>
        string.IsNullOrWhiteSpace(FullName)
            ? string.IsNullOrWhiteSpace(Username) ? WarehouseCode : Username
            : FullName;

    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;
}

// ── Quality ─────────────────────────────────────────────────────────────────────

public class VanMarginQuality
{
    public int LineCount { get; set; }
    public int PostedLineCount { get; set; }
    public int ItemsWithNoDescription { get; set; }
    public bool PostingJobEnabled { get; set; }

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
