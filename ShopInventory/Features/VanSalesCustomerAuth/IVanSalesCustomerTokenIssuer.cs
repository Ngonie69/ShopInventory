using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <summary>
/// Mints the access token a signed-in van sales customer carries.
/// </summary>
/// <remarks>
/// Deliberately narrow, and deliberately not part of <c>IAuthService</c>: that interface issues
/// tokens for employees, and the two subjects must not become interchangeable through a shared
/// method that takes "whoever". Everything about a customer token — its role, its claims, its
/// lifetime — is different, and the only thing it shares with a staff token is the signing key.
/// </remarks>
public interface IVanSalesCustomerTokenIssuer
{
    /// <summary>
    /// Issue an access token for <paramref name="account"/>, trading as
    /// <paramref name="routeCustomerCode"/>.
    /// </summary>
    /// <returns>The signed token and the instant it expires.</returns>
    (string AccessToken, DateTime ExpiresAtUtc) IssueAccessToken(
        VanSalesCustomerAccountEntity account,
        string routeCustomerCode);
}
