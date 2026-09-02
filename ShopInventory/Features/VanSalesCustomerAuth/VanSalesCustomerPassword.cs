namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <summary>
/// Hashes and checks the password a shop signs into the ordering app with.
/// </summary>
/// <remarks>
/// BCrypt at the same work factor the staff sign-in uses, for the same reason: the cost of a guess
/// has to be paid by whoever is guessing. This is not <see cref="VanSalesCustomerOtpCode"/>'s keyed
/// HMAC because the two credentials fail differently — a six-digit code has a million values and is
/// protected by expiry and an attempt cap, while a password is long-lived, chosen by a person, and
/// has to survive the table itself being stolen.
/// <para>
/// Kept beside the slice that uses it rather than reaching into <c>AuthService</c>. That class hashes
/// employee passwords, and a shared helper is how a change made for staff quietly becomes a change
/// to how customers authenticate.
/// </para>
/// </remarks>
public static class VanSalesCustomerPassword
{
    /// <summary>
    /// Shortest password an operator may set.
    /// </summary>
    /// <remarks>
    /// Eight, and no composition rules. The people typing this are shopkeepers on cheap handsets,
    /// and a rule that demands a symbol produces a password on a note beside the till — which is a
    /// worse outcome than a longer plain one. The attempt cap and lockout on the account are what
    /// actually stop guessing.
    /// </remarks>
    public const int MinimumLength = 8;

    /// <summary>
    /// Longest accepted, because BCrypt silently truncates past 72 bytes.
    /// </summary>
    /// <remarks>
    /// Refused rather than truncated: a password accepted at 100 characters and compared on its
    /// first 72 is one where the last 28 do nothing, and nobody is told.
    /// </remarks>
    public const int MaximumLength = 72;

    /// <summary>Work factor. Matches the staff sign-in so neither is quietly the weaker one.</summary>
    private const int WorkFactor = 12;

    private static readonly Lazy<string> Decoy =
        new(() => Hash(Guid.NewGuid().ToString("N")), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A hash of nothing anybody knows, to check a supplied password against when there is no
    /// account.
    /// </summary>
    /// <remarks>
    /// Sign-in says the same thing whether the number is unregistered or the password wrong, but
    /// saying it in a fifth of the time gives the answer away regardless: BCrypt at work factor 12
    /// takes long enough to time over a network. Verifying against this costs the same as verifying
    /// against a real account, so the two are indistinguishable by clock as well as by wording.
    /// <para>
    /// Generated once per process from a value nothing else holds, so it cannot match a password
    /// anyone might type.
    /// </para>
    /// </remarks>
    public static string DecoyHash => Decoy.Value;

    public static string Hash(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    /// <summary>
    /// Whether <paramref name="supplied"/> is the password behind <paramref name="storedHash"/>.
    /// </summary>
    /// <remarks>
    /// False for a missing or malformed hash rather than throwing. An account that has never been
    /// given a password reaches this, and it must answer like any other wrong password — an
    /// exception here would surface as a 500 and tell the caller they had found a real number
    /// without one.
    /// </remarks>
    public static bool Verify(string? supplied, string? storedHash)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        try
        {
            return BCrypt.Net.BCrypt.Verify(supplied, storedHash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}
