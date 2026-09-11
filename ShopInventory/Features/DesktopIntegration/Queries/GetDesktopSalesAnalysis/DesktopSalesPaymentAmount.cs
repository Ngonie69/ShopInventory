namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>One payment method's part of a breakdown row.</summary>
public sealed record DesktopSalesPaymentAmount(
    string PaymentMethod,
    int SalesCount,
    decimal TotalAmount);
