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

    /// <summary>The last four digits, for screens and logs that must not repeat the whole number.</summary>
    public static string Mask(string? e164) => VanSalesCustomerPhone.Mask(e164 ?? string.Empty);
}
