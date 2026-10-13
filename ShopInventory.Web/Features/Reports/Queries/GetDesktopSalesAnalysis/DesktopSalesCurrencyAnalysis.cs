namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>Every figure the analysis states for the sales made in one currency.</summary>
public sealed class DesktopSalesCurrencyAnalysis
{
    public string Currency { get; set; } = string.Empty;

    public int SalesCount { get; set; }

    /// <summary>What the sales came to, VAT included.</summary>
    public decimal TotalAmount { get; set; }

    public decimal VatAmount { get; set; }

    public decimal NetAmount { get; set; }



    public decimal AverageSale { get; set; }

    public decimal QuantitySold { get; set; }

    public int DaysTraded { get; set; }

    public int DistinctItems { get; set; }

    public List<DesktopSalesPaymentMethodRow> ByPaymentMethod { get; set; } = [];

    public List<DesktopSalesDayRow> ByDay { get; set; } = [];

    public List<DesktopSalesHourRow> ByHour { get; set; } = [];

    public List<DesktopSalesBreakdownRow> ByWarehouse { get; set; } = [];

    /// <summary>Each business partner the sales were made as, keyed by CardCode and labelled by its name.</summary>
    public List<DesktopSalesBreakdownRow> ByBusinessPartner { get; set; } = [];

    /// <summary>
    /// Van analysis only: each business partner the vans billed as, keyed by CardCode. The API labels it by
    /// code; the query handler swaps in the partner's name from the local cache.
    /// </summary>
    public List<DesktopSalesBreakdownRow> ByVanAccount { get; set; } = [];

    public List<DesktopSalesBreakdownRow> BySource { get; set; } = [];

    public List<DesktopSalesBreakdownRow> ByOperator { get; set; } = [];

    /// <summary>
    /// The best sellers of each line of business, one business after another. Never ranked together:
    /// shops, vending and vans sell different ranges. Read them through <see cref="ItemGroups"/>.
    /// </summary>
    public List<DesktopSalesItemRow> TopItems { get; set; } = [];

    /// <summary>One row per line of business that sold anything, in the order <c>TopItems</c> lists them.</summary>
    public List<DesktopSalesBusinessItems> ItemsByBusiness { get; set; } = [];

    /// <summary>
    /// The lines of business to draw best sellers for, each with its own items.
    /// </summary>
    /// <remarks>
    /// An API that predates the split names no business on any row; its items are then one unnamed group,
    /// so a page drawn against it still lists them rather than going blank.
    /// </remarks>
    public List<(DesktopSalesBusinessItems Business, List<DesktopSalesItemRow> Items)> ItemGroups()
    {
        if (ItemsByBusiness.Count == 0)
        {
            return TopItems.Count == 0
                ? []
                :
                [
                    (new DesktopSalesBusinessItems
                    {
                        DistinctItems = DistinctItems,
                        QuantitySold = QuantitySold,
                        NetAmount = NetAmount
                    }, TopItems)
                ];
        }

        return ItemsByBusiness
            .Select(business => (business, TopItems.Where(item => item.Business == business.Business).ToList()))
            .ToList();
    }

    /// <summary>Sales in the same number of days just before, under the same filters.</summary>
    public int PreviousSalesCount { get; set; }

    /// <summary>What those came to; zero means none, which is not a change to state.</summary>
    public decimal PreviousTotalAmount { get; set; }
}
