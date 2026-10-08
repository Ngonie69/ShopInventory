using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>
/// The words sent with a document, and the name the customer sees on its file.
/// </summary>
internal static partial class CustomerDocumentCaption
{
    /// <summary>OpenWA refuses a caption longer than this.</summary>
    public const int MaxCaptionLength = 1024;

    private const int MaxFileNameLength = 200;

    public static string Render(
        string template,
        string? customerName,
        string documentNumber,
        DateTime? documentDate,
        string? currency,
        decimal? total)
    {
        var text = (string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template)
            .Replace("{customer}", Clean(customerName) ?? "customer", StringComparison.Ordinal)
            .Replace("{number}", documentNumber, StringComparison.Ordinal)
            .Replace("{date}", documentDate?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal)
            .Replace("{currency}", Clean(currency) ?? string.Empty, StringComparison.Ordinal)
            .Replace("{total}", total?.ToString("N2", CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal);

        text = RepeatedSpaces().Replace(text, " ").Replace(" .", ".", StringComparison.Ordinal).Trim();

        if (text.Length <= MaxCaptionLength)
        {
            return text;
        }

        // Whole words only: a caption cut mid-word reads as a fault.
        var cut = text.LastIndexOf(' ', MaxCaptionLength - 1);
        return text[..(cut > 0 ? cut : MaxCaptionLength - 1)] + "…";
    }

    public static string FileName(string template, string documentNumber)
    {
        var name = (string.IsNullOrWhiteSpace(template) ? DefaultFileNameTemplate : template)
            .Replace("{number}", documentNumber, StringComparison.Ordinal);

        var safe = new StringBuilder(name.Length);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var character in name)
        {
            safe.Append(invalid.Contains(character) ? '-' : character);
        }

        var result = safe.ToString().Trim();
        if (!result.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            result += ".pdf";
        }

        return result.Length <= MaxFileNameLength
            ? result
            : result[..(MaxFileNameLength - 4)] + ".pdf";
    }

    private const string DefaultTemplate =
        "Good day {customer}. Please find attached Kefalos tax invoice {number} dated {date} for {currency} {total}.";

    private const string DefaultFileNameTemplate = "Kefalos-Invoice-{number}.pdf";

    /// <summary>
    /// SAP's "all currencies" marker is not a currency, and a blank name is no greeting.
    /// </summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed == "##" ? null : trimmed;
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedSpaces();
}
