namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>The takings of one payment method.</summary>
public sealed class DesktopSalesPaymentMethodRow
{
    public string PaymentMethod { get; set; } = string.Empty;

    public int SalesCount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal VatAmount { get; set; }

    public decimal AmountPaid { get; set; }

    public decimal ChangeGiven { get; set; }

    public decimal AverageSale { get; set; }

    public decimal ShareOfValuePercent { get; set; }

    public decimal ShareOfCountPercent { get; set; }

    /// <summary>Sales carrying no payment reference; only meaningful for a wallet tender.</summary>
    public int WithoutReferenceCount { get; set; }
}
