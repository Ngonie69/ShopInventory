namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <summary>A customer sign-in as an operator sees it. Never carries a code or a token.</summary>
public sealed record VanSalesCustomerAccountResult(
    int Id,
    int RouteCustomerId,
    string RouteCustomerCode,
    string RouteCustomerName,
    string PhoneE164,
    string? DisplayName,
    bool IsActive,
    bool IsLockedOut,
    DateTime? LastLoginAt,
    DateTime CreatedAt);
