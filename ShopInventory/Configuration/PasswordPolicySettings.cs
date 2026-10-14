namespace ShopInventory.Configuration;

/// <summary>
/// The parts of the password policy that are about an account over time, for staff accounts.
/// </summary>
/// <remarks>
/// Bound from <c>Security:PasswordPolicy</c>. What a password must look like is not configurable and
/// is not here — see <see cref="Common.Security.PasswordPolicy"/>.
/// </remarks>
public class PasswordPolicySettings
{
    public const string SectionName = "Security:PasswordPolicy";

    /// <summary>
    /// How long a password lasts before it has to be renewed. Zero switches renewal off.
    /// </summary>
    public int MaxAgeMonths { get; set; } = 3;

    /// <summary>
    /// How many days before the renewal date the account starts being told about it.
    /// </summary>
    public int ExpiryWarningDays { get; set; } = 14;

    /// <summary>
    /// How many of an account's most recent passwords, the current one included, may not be chosen
    /// again.
    /// </summary>
    /// <remarks>
    /// Without it a three-month renewal is satisfied by swapping between two passwords for ever.
    /// </remarks>
    public int RememberedPasswords { get; set; } = 5;

    /// <summary>
    /// Whether a password typed by an administrator has to be replaced by its owner at the next
    /// sign-in.
    /// </summary>
    /// <remarks>
    /// A password somebody else chose is, by construction, known to two people.
    /// </remarks>
    public bool RequireChangeAfterAdminSet { get; set; } = true;

    /// <summary>
    /// Roles whose passwords are never aged and never flagged for change.
    /// </summary>
    /// <remarks>
    /// For accounts no person signs in to. A service that authenticates with a stored password
    /// cannot be shown a "choose a new one" screen; it just stops, three months after the last
    /// person to remember it has moved on.
    /// <para>
    /// Empty here and listed in <c>appsettings.json</c>, because the configuration binder appends
    /// to a collection that already holds values rather than replacing it.
    /// </para>
    /// </remarks>
    public List<string> ExpiryExemptRoles { get; set; } = [];

    /// <summary>Individual usernames exempt for the same reason.</summary>
    public List<string> ExpiryExemptUsernames { get; set; } = [];

    /// <summary>
    /// Whether an account that owes a password change is held to the password screens on every
    /// client of the API, and not only on the web portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The portal always holds such an account to its Security page: it reads the flag on the
    /// sign-in reply and is built to act on it. The tills and the handsets were written before the
    /// flag existed and ignore it, so for them the only enforcement there can be is the API
    /// refusing their requests — see <see cref="Middleware.PasswordChangeGateMiddleware"/>.
    /// </para>
    /// <para>
    /// Off until each of those apps has been seen to cope with the refusal. On a till or a van at
    /// the start of a trading day, "cope" means the operator is told why nothing loads and can
    /// reach the app's own change-password screen, and that has to be watched happening on the
    /// device rather than assumed from here.
    /// </para>
    /// </remarks>
    public bool EnforceOnApiClients { get; set; }
}
