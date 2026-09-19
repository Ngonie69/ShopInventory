using System.Net.Mail;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Reads and checks a mapping's recipient list, stored as one string separated by commas.
/// </summary>
public static class IncomingPaymentGlMappingAddresses
{
    public static List<string> Parse(string? value) =>
        (value ?? string.Empty)
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IEnumerable<string> Invalid(string? value) =>
        Parse(value).Where(address => !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address);

    /// <summary>The list in its stored form, or null when empty.</summary>
    public static string? Normalise(string? value)
    {
        var addresses = Parse(value);
        return addresses.Count == 0 ? null : string.Join(", ", addresses);
    }
}
