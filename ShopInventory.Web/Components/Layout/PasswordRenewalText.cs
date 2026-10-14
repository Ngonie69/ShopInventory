using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Layout;

/// <summary>
/// What the portal says to an account whose password is due, or nearly due, for renewal.
/// </summary>
/// <remarks>
/// Apart from the markup so the wording and the "which page is this" rule can be tested without
/// rendering anything, as <see cref="SapOutageBannerText"/> is.
/// </remarks>
public static class PasswordRenewalText
{
    /// <summary>Where a password is changed. The query opens the form without a click.</summary>
    public const string ChangePage = "security?renew=1";

    /// <summary>
    /// Whether a base-relative path is the page a password is changed on, whatever its query.
    /// </summary>
    public static bool IsChangePage(string baseRelativePath)
    {
        var path = baseRelativePath.Split('?', '#')[0].Trim('/');
        return path.Equals("security", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether an account that owes a change is left where it is: on the page that makes the
    /// change, or on its way out to sign in again.
    /// </summary>
    /// <remarks>
    /// Sign-in is exempt because that is where a changed password sends the browser. Pulling it
    /// back from there would ask for the change that had just been made.
    /// </remarks>
    public static bool MayStayOn(string baseRelativePath)
    {
        var path = baseRelativePath.Split('?', '#')[0].Trim('/');
        return IsChangePage(baseRelativePath) || path.Equals("login", StringComparison.OrdinalIgnoreCase);
    }

    public static string Headline(PasswordStandingInfo standing)
    {
        if (standing.ChangeRequired)
        {
            return standing.Reason == PasswordStandingInfo.Expired
                ? "Your password has expired"
                : "Choose a new password to carry on";
        }

        return standing.ExpiresInDays switch
        {
            null => "Your password is due for renewal soon",
            <= 1 => "Your password expires today",
            var days => $"Your password expires in {days} days"
        };
    }

    public static string Detail(PasswordStandingInfo standing)
    {
        if (!standing.ChangeRequired)
        {
            return "Passwords are renewed every three months. Changing it now saves being stopped at sign-in.";
        }

        return standing.Reason == PasswordStandingInfo.Expired
            ? "Passwords are renewed every three months. Set a new one below — nothing else opens until you do."
            : "This one was set for you, or an administrator has asked for it to be changed. Set your own below — nothing else opens until you do.";
    }
}
