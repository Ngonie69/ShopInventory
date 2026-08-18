namespace ShopInventory.Web.Models;

/// <summary>
/// The margin report, mirroring the API's <c>VanMarginReportResult</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored like every other API DTO here, so nullability has to match exactly: a property
/// declared non-nullable against a value the API can send as null makes System.Text.Json throw
/// inside <c>GetFromJsonAsync</c>, the service's catch turns it into null, and the page reports "no
/// data".
///
/// Two shapes here carry the report's whole discipline and are worth stating where a reader of the
/// DTOs meets them.
///
/// <see cref="VanMarginItem.UnitCost"/> is null rather than zero when no cost was found. A zero
/// would report an item that costs nothing to sell, and B1 genuinely does leave the cost column at
/// zero on a line whose item has no valuation — so the difference between "we do not know" and "it
/// is free" is load-bearing.
///
/// <see cref="VanMarginMoney"/> rows exist only for a currency whose revenue is in the same money as
/// the cost. A currency that does not match produces no row at all rather than a row with a null
/// margin, because a row of nulls beside a row of figures invites a reader to fill the gap in their
/// head.
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

// ── Margin ──────────────────────────────────────────────────────────────────────

/// <summary>
/// One currency's margin: the revenue that could be costed, the cost, and the difference.
/// </summary>
public class VanMarginMoney
{
    public string Currency { get; set; } = string.Empty;
    public int LineCount { get; set; }
    public decimal Revenue { get; set; }
    public decimal Cost { get; set; }

    public decimal Margin => Revenue - Cost;

    /// <summary>
    /// Margin as a share of revenue. Null when nothing sold in this currency. Legitimately negative
    /// where an item sold below cost, which is a finding rather than an error.
    /// </summary>
    public double? MarginRate => Revenue != 0 ? (double)(Margin / Revenue) : null;
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
    public string? CostCurrency { get; set; }
    public List<VanMarginMoney> MarginByCurrency { get; set; } = [];

    /// <summary>
    /// The share of lines whose sale reached SAP, and which a cost could therefore be found for.
    /// Null when nothing sold — a period with no lines has no costable share, and 0% would read as
    /// an estate whose posting has completely failed rather than one that had a quiet week.
    /// </summary>
    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;

    /// <summary>
    /// Whether any margin could be stated at all. A field rather than something a reader infers from
    /// a column of dashes, because a reader who infers it wrongly infers that the vans sell at cost.
    /// </summary>
    public bool MarginAvailable => MarginByCurrency.Count > 0;
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
    public string? CostCurrency { get; set; }
    public List<VanMarginMoney> MarginByCurrency { get; set; } = [];

    public string DisplayName =>
        string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;

    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;

    /// <summary>
    /// Whether a usable cost was found for this item. Null unit cost and not zero: a zero would
    /// report an item that costs nothing to sell.
    /// </summary>
    public bool HasCost => UnitCost is > 0;
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
    public int ItemsWithoutCost { get; set; }
    public int ItemCount { get; set; }
    public string? CostCurrency { get; set; }
    public bool CostAttempted { get; set; }
    public List<string> CurrenciesWithoutMatchingCost { get; set; } = [];
    public bool PostingJobEnabled { get; set; }

    public int UnpostedLineCount => LineCount - PostedLineCount;

    /// <summary>
    /// Whether a cost was established at all. False means every margin figure on the report is
    /// absent — not zero.
    /// </summary>
    public bool CostAvailable => CostCurrency is not null;

    /// <summary>
    /// Clean means the margin describes the whole period: everything posted, everything costed, and
    /// no currency left out. That is a high bar and most periods will not meet it, which is the
    /// point — a margin over two thirds of the trading is a different number from a margin over all
    /// of it, and only this says which one is on the screen.
    /// </summary>
    public bool IsClean =>
        CostAvailable
        && UnpostedLineCount == 0
        && ItemsWithoutCost == 0
        && CurrenciesWithoutMatchingCost.Count == 0
        && ItemsWithNoDescription == 0;

    public IEnumerable<string> Caveats
    {
        get
        {
            if (!CostAttempted)
            {
                yield return
                    "Costs were not fetched for this run, so no margin is stated. Everything below is "
                    + "revenue and quantity.";
            }
            else if (!CostAvailable)
            {
                yield return
                    "No cost could be read from SAP for this period, so no margin is stated. Either "
                    + "SAP was unreachable or the company currency could not be established — and "
                    + "without knowing what a cost is denominated in, subtracting it from revenue "
                    + "would be arithmetic between two kinds of money.";
            }

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
                    + "reached SAP and therefore carry no cost. Any margin here describes the "
                    + "remainder, not the period.";
            }

            if (CurrenciesWithoutMatchingCost.Count > 0)
            {
                yield return
                    "No margin is stated for "
                    + string.Join(", ", CurrenciesWithoutMatchingCost)
                    + $". SAP states a line's cost in {CostCurrency ?? "the company currency"}, and a "
                    + "margin across two currencies would be a subtraction between two kinds of "
                    + "money. Their revenue is reported without it.";
            }

            if (ItemsWithoutCost > 0)
            {
                yield return
                    $"{ItemsWithoutCost:N0} of {ItemCount:N0} item(s) have no usable cost — either "
                    + "nothing they sold reached SAP, or SAP carried no valuation for them on the "
                    + "lines that did. They are reported with revenue and no margin rather than being "
                    + "treated as pure profit.";
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
