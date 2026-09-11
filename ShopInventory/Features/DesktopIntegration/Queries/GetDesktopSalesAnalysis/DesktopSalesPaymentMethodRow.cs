namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// The takings of one payment method.
/// </summary>
/// <remarks>
/// <para><c>PaymentMethod</c>: The tender as <c>TenderTypes.ReportingName</c> names it, so casing and
/// legacy spellings fold into one row and a sale with no tender recorded is reported as exactly
/// that.</para>
/// <para><c>ShareOfValuePercent</c>: This method's share of the currency's total value, to one
/// decimal.</para>
/// <para><c>ShareOfCountPercent</c>: This method's share of the currency's sales, to one
/// decimal.</para>
/// <para><c>WithoutReferenceCount</c>: Sales carrying no payment reference. Only meaningful for a
/// wallet tender, where the reference is what ties the receipt to money that arrived; a cash sale never
/// has one.</para>
/// </remarks>
public sealed record DesktopSalesPaymentMethodRow(
    string PaymentMethod,
    int SalesCount,
    decimal TotalAmount,
    decimal VatAmount,
    decimal AmountPaid,
    decimal ChangeGiven,
    decimal AverageSale,
    decimal ShareOfValuePercent,
    decimal ShareOfCountPercent,
    int WithoutReferenceCount);
