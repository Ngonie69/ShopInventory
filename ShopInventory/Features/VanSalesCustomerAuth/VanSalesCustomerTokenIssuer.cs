using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ShopInventory.Configuration;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <inheritdoc />
public sealed class VanSalesCustomerTokenIssuer(
    IOptions<JwtSettings> jwtSettings,
    IOptions<VanSalesCustomerAuthSettings> authSettings) : IVanSalesCustomerTokenIssuer
{
    private readonly JwtSettings _jwt = jwtSettings.Value;
    private readonly VanSalesCustomerAuthSettings _settings = authSettings.Value;

    public (string AccessToken, DateTime ExpiresAtUtc) IssueAccessToken(
        VanSalesCustomerAccountEntity account,
        string routeCustomerCode)
    {
        ArgumentNullException.ThrowIfNull(account);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiresAt = DateTime.UtcNow.AddMinutes(_settings.AccessTokenExpirationMinutes);

        // The role is the only one this token carries, and it is absent from ApiAccessRoles — which
        // is what refuses this token at every staff endpoint. See VanSalesCustomerAuthPolicyTests.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new(ClaimTypes.Name, account.PhoneE164),
            new(ClaimTypes.Role, ApplicationRoles.VanSalesCustomer),
            new(VanSalesCustomerClaims.AccountId, account.Id.ToString()),
            new(VanSalesCustomerClaims.CustomerCode, routeCustomerCode),
            new(JwtRegisteredClaimNames.Sub, account.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(
                JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
