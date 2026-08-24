namespace ShopInventory.Models;

/// <summary>
/// The claims that carry a van sales customer's identity on their access token.
/// </summary>
/// <remarks>
/// Named constants rather than string literals because these are read in three places that must
/// agree exactly — the token is written in <c>AuthService</c>, the "VanSalesCustomerAccess" policy
/// requires <see cref="CustomerCode"/> in <c>Program.cs</c>, and every customer-facing handler
/// resolves the caller from <see cref="AccountId"/>. A typo in any one of them is either a customer
/// who cannot sign in or, worse, a handler that silently falls back to trusting the request body.
/// </remarks>
public static class VanSalesCustomerClaims
{
    /// <summary>The <see cref="Entities.VanSalesCustomerAccountEntity"/> primary key, as a string.</summary>
    public const string AccountId = "vansales_customer_id";

    /// <summary>
    /// The <see cref="Entities.RouteCustomerEntity.Code"/> the account trades as. Required by the
    /// access policy, so its presence can be assumed by anything the policy has admitted.
    /// </summary>
    public const string CustomerCode = "vansales_customer_code";
}
