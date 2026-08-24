using System.Security.Cryptography;
using System.Text;

namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <summary>
/// Generates one-time codes and turns them into something safe to store.
/// </summary>
/// <remarks>
/// A six-digit code has a million possibilities, which is a rounding error to a machine. That fact
/// shapes everything here:
/// <list type="bullet">
/// <item>The code is drawn from <see cref="RandomNumberGenerator"/>, not <c>Random</c> — a
/// predictable code is no credential at all.</item>
/// <item>It is stored as a <em>keyed</em> HMAC rather than a bare digest. A leaked table of plain
/// SHA-256 hashes of six-digit codes is reversible by exhaustion in moments; without the key, the
/// HMAC is not.</item>
/// <item>Comparison is fixed-time, so the answer cannot be recovered a character at a time.</item>
/// </list>
/// The remaining defences are not here but around this: a short expiry, a single use, a cap on
/// attempts, and a lockout on the account.
/// </remarks>
public static class VanSalesCustomerOtpCode
{
    /// <summary>Generate a numeric code of <paramref name="length"/> digits, uniformly distributed.</summary>
    public static string Generate(int length)
    {
        if (length < 4)
        {
            length = 4;
        }

        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            // Per-digit rather than one bounded integer: this keeps the distribution uniform and
            // allows a leading zero, which a customer will happily type but an int would eat.
            builder.Append((char)('0' + RandomNumberGenerator.GetInt32(0, 10)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Keyed hash of <paramref name="code"/> for <paramref name="phoneE164"/>.
    /// </summary>
    /// <remarks>
    /// The phone is bound into the hash so a row cannot be lifted from one number to another: the
    /// stored value only verifies for the number it was issued to.
    /// </remarks>
    public static string Hash(string phoneE164, string code, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key ?? string.Empty);
        var payload = Encoding.UTF8.GetBytes($"{phoneE164}:{code}");
        return Convert.ToHexString(HMACSHA256.HashData(keyBytes, payload));
    }

    /// <summary>Fixed-time comparison of a supplied code against a stored hash.</summary>
    public static bool Verify(string phoneE164, string suppliedCode, string storedHash, string key)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(suppliedCode))
        {
            return false;
        }

        var computed = Hash(phoneE164, suppliedCode, key);

        // Compare the bytes, not the strings: string equality returns early on the first difference
        // and leaks how much of the code was right.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(storedHash));
    }
}
