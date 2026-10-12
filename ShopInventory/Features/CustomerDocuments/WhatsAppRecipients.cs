using ShopInventory.Features.VanSalesCustomerAuth;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>
/// One way of writing a customer's WhatsApp number, and the forms OpenWA wants it in.
/// </summary>
/// <remarks>
/// The normalising itself is the van-sales customer sign-in's (<see cref="VanSalesCustomerPhone"/>),
/// deliberately: a shop whose number is on its app account and on its invoice contact must be one
/// number to both, or an opt-out on one would not reach the other.
/// </remarks>
internal static class WhatsAppRecipients
{
    public static bool TryNormalise(string? input, string defaultCountryCode, out string e164)
        => VanSalesCustomerPhone.TryNormalise(input, defaultCountryCode, out e164);

    /// <summary>The country code and number, digits only — what OpenWA's number check takes.</summary>
    public static string Digits(string e164) => new(e164.Where(char.IsDigit).ToArray());

    /// <summary>The individual chat OpenWA sends to: <c>263771234567@c.us</c>.</summary>
    public static string ChatId(string e164) => Digits(e164) + "@c.us";

    /// <summary>
    /// Whether a number given as a customer's is the one documents are sent from. It never is a
    /// customer's: the customer receives nothing, saved on a shop it takes every invoice after, and
    /// the gateway answered 500 to the one such send that was tried. So it is caught before it is
    /// saved or sent to.
    /// </summary>
    /// <param name="e164">The customer's number, normalised.</param>
    /// <param name="sendingPhone">
    /// The session's own number as the gateway gives it — digits, country code first — or null when
    /// the gateway could not be asked, which is never a match.
    /// </param>
    public static bool IsSendingNumber(string? e164, string? sendingPhone)
    {
        if (string.IsNullOrWhiteSpace(e164) || string.IsNullOrWhiteSpace(sendingPhone))
            return false;

        // The gateway gives the bare number today; a chat id (263…@c.us) or a device suffix (263…:12)
        // is the same account, and the part after the mark is not part of the number.
        var own = Digits(sendingPhone.Split('@', ':')[0]);

        return own.Length > 0 && string.Equals(own, Digits(e164), StringComparison.Ordinal);
    }

    /// <summary>The last four digits, for screens and logs that must not repeat the whole number.</summary>
    public static string Mask(string? e164) => VanSalesCustomerPhone.Mask(e164 ?? string.Empty);
}
