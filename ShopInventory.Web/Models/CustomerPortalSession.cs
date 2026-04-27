namespace ShopInventory.Web.Models;

public sealed record CustomerPortalSession(
    CustomerInfo Customer,
    IReadOnlyList<LinkedAccountInfo> LinkedAccounts,
    IReadOnlyList<string> AccessibleCardCodes);
