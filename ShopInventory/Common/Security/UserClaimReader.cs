using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace ShopInventory.Common.Security;

internal static class UserClaimReader
{
    public static Guid? GetUserId(ClaimsPrincipal? user)
    {
        if (user is null)
        {
            return null;
        }

        var candidateValues = user.FindAll(ClaimTypes.NameIdentifier)
            .Select(claim => claim.Value)
            .Concat(user.FindAll(JwtRegisteredClaimNames.Sub).Select(claim => claim.Value));

        foreach (var candidateValue in candidateValues)
        {
            if (Guid.TryParse(candidateValue, out var userId) && userId != Guid.Empty)
            {
                return userId;
            }
        }

        return null;
    }
}