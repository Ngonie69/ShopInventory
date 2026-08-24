using System.Security.Cryptography;
using System.Text;

namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <summary>
/// The customer refresh token as a value: 64 random bytes issued once, and stored only as a digest.
/// </summary>
/// <remarks>
/// A plain SHA-256 is right here where it is wrong for the OTP: this value has 512 bits of entropy,
/// so there is no dictionary to exhaust and nothing a key would add. The same reasoning — and the
/// same shape — as the staff refresh tokens hashed by <c>AuthService</c>.
/// </remarks>
public static class VanSalesCustomerRefreshTokenValue
{
    /// <summary>Mint a new token value. This is the only time it exists in readable form.</summary>
    public static string Generate()
    {
        var bytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Digest of a token value, as stored and as looked up.</summary>
    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
