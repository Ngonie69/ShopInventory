namespace ShopInventory.Features.Password;

/// <summary>
/// Where a staff account stands against the renewal half of the password policy.
/// </summary>
/// <param name="ChangedAtUtc">When the password in use was set.</param>
/// <param name="ExpiresAtUtc">
/// The moment from which it must be replaced; null for an account whose password does not age.
/// </param>
/// <param name="ChangeRequired">Whether the owner has to choose a new one now.</param>
/// <param name="Reason">
/// <see cref="Expired"/> or <see cref="Required"/> when a change is owed, otherwise null.
/// </param>
/// <param name="ExpiresInDays">
/// Whole days left, counted up so that "0" is never shown for a password that still works; null
/// when it does not age. Negative values are not produced — an overdue password reads 0.
/// </param>
public sealed record PasswordStanding(
    DateTime ChangedAtUtc,
    DateTime? ExpiresAtUtc,
    bool ChangeRequired,
    string? Reason,
    int? ExpiresInDays)
{
    /// <summary>The password is older than the policy allows.</summary>
    public const string Expired = "Expired";

    /// <summary>Somebody other than the owner set it, or an administrator asked for it to go.</summary>
    public const string Required = "Required";

    /// <summary>The claim an access token carries while its account owes a change.</summary>
    public const string ClaimType = "pwd_change";

    /// <summary>A sentence for whoever is looking at a screen that will not load.</summary>
    public string? Message => Reason switch
    {
        Expired => "Your password is more than three months old and has to be renewed. " +
                   "Change it under your profile or security settings, then sign in again.",
        Required => "This account's password has to be changed before it can be used. " +
                    "Change it under your profile or security settings, then sign in again.",
        _ => null
    };
}
