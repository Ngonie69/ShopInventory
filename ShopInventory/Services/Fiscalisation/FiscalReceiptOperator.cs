namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// The ShopInventory user who raised a document, stated to the fiscalisation platform as the receipt's operator so
/// its Receipts page names them. ShopInventory posts to SAP under one SAP account of its own, which names nobody, and
/// without this the platform showed that account as the raiser of every invoice made here (798346, 2026-10-24: SAP
/// user "Software"). The platform sends it to FDMS as the receipt's <c>userInfo</c>, as it does a van driver's.
/// <para>
/// State it only for a document ShopInventory raised. Fiscalising one someone keyed into SAP names nobody: the
/// platform then names the SAP user who created it, and a name here would replace them.
/// </para>
/// </summary>
/// <param name="Username">The user's ShopInventory username.</param>
/// <param name="FullName">First and last name, when the user has them.</param>
public sealed record FiscalReceiptOperator(string Username, string? FullName)
{
    /// <summary>The platform's limits on the two fields. It refuses a longer one rather than cutting it.</summary>
    public const int MaxUsernameLength = 100;

    /// <inheritdoc cref="MaxUsernameLength"/>
    public const int MaxFullNameLength = 250;

    /// <summary>The operator for a user, or null when there is no username to name.</summary>
    public static FiscalReceiptOperator? For(string? username, string? firstName = null, string? lastName = null)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var fullName = string.Join(
            ' ',
            new[] { firstName, lastName }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        return new(username.Trim(), fullName.Length == 0 ? null : fullName);
    }

    /// <summary>Names this user as the operator of a request bound for the platform.</summary>
    public void ApplyTo(SubmitReceiptApiRequest request)
    {
        var username = Clip(Username.Trim(), MaxUsernameLength);
        request.Username = username;
        request.UserNameSurname = string.IsNullOrWhiteSpace(FullName) ? username : Clip(FullName.Trim(), MaxFullNameLength);
    }

    private static string Clip(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
