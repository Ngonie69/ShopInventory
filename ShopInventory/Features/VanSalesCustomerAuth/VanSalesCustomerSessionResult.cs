namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <summary>A signed-in customer's session, as the app receives it.</summary>
public sealed record VanSalesCustomerSessionResult(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAtUtc,
    VanSalesCustomerSummary Customer);

/// <summary>
/// Who the app is signed in as. Enough to greet the customer and label their orders, and no more —
/// this is a token payload's worth of a business partner, not the business partner.
/// </summary>
public sealed record VanSalesCustomerSummary(
    int AccountId,
    string CustomerCode,
    string CustomerName,
    string? DisplayName,
    string? Phone,
    string? Address);
