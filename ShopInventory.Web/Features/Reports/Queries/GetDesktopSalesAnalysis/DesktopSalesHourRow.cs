namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>The takings rung up in one hour of the counter's day (CAT), summed across the period.</summary>
public sealed class DesktopSalesHourRow
{
    public int Hour { get; set; }

    public int SalesCount { get; set; }

    public decimal TotalAmount { get; set; }

    /// <summary>
    /// What has been credited back on these sales, tax-inclusive like <see cref="TotalAmount"/>; 0 when
    /// nothing has. <see cref="TotalAmount"/> is not reduced by it: it stays what was rung up.
    /// </summary>
    public decimal CreditedAmount { get; set; }

    /// <summary><see cref="TotalAmount"/> less <see cref="CreditedAmount"/>, as the API states it.</summary>
    public decimal NetOfCredits { get; set; }

    /// <summary>SAP's booked gross profit on this row's posted sales; null when none is costed or margin was not asked for.</summary>
    public decimal? GrossProfit { get; set; }

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on.</summary>
    public decimal? CostedNetAmount { get; set; }

    /// <summary>How many of this row's sales <see cref="GrossProfit"/> was measured on.</summary>
    public int CostedSalesCount { get; set; }

    /// <summary><see cref="GrossProfit"/> as a percentage of <see cref="CostedNetAmount"/>, as the API states it.</summary>
    public decimal? MarginPercent { get; set; }
}
