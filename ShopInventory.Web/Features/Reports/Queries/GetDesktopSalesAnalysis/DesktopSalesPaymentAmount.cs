namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>One payment method's part of a breakdown row.</summary>
public sealed class DesktopSalesPaymentAmount
{
    public string PaymentMethod { get; set; } = string.Empty;

    public int SalesCount { get; set; }

    public decimal TotalAmount { get; set; }
}
