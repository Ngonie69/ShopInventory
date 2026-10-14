namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>The takings of one payment method.</summary>
public sealed class DesktopSalesPaymentMethodRow
{
    public string PaymentMethod { get; set; } = string.Empty;

    public int SalesCount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal VatAmount { get; set; }



    public decimal AverageSale { get; set; }

    public decimal ShareOfValuePercent { get; set; }

    public decimal ShareOfCountPercent { get; set; }

    /// <summary>Sales carrying no payment reference; only meaningful for a wallet tender.</summary>
    public int WithoutReferenceCount { get; set; }

    /// <summary>
    /// What has been credited back on these sales, tax-inclusive like <see cref="TotalAmount"/>; 0 when
    /// nothing has. <see cref="TotalAmount"/> is not reduced by it: it stays what was rung up.
    /// </summary>
    public decimal CreditedAmount { get; set; }

    /// <summary><see cref="TotalAmount"/> less <see cref="CreditedAmount"/>, as the API states it.</summary>
    public decimal NetOfCredits { get; set; }
}
