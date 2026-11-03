using System.Globalization;
using System.Text;

namespace ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppReport;

/// <summary>
/// The report as WhatsApp text: the two tables of the office's daily sheet, one "label: figure" line per
/// row, headings and totals in bold.
/// </summary>
/// <remarks>
/// Plain lines rather than a monospaced table. A phone wraps a padded table at its own width and the
/// columns fall apart; a line per row reads the same on every screen.
/// </remarks>
public static class DailySalesWhatsAppMessage
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Build(
        DateTime day,
        IReadOnlyList<DailySalesWhatsAppVanLine> vanLines,
        decimal vanTotal,
        IReadOnlyList<DailySalesWhatsAppCurrencyAmount> vanOther,
        DailySalesWhatsAppShop? shop)
    {
        var date = DayLabel(day);
        var text = new StringBuilder();

        text.Append(Bold($"Van Daily Sales ({date})")).Append('\n');
        if (vanLines.Count == 0)
        {
            text.Append("No van sales recorded.").Append('\n');
        }

        foreach (var line in vanLines)
        {
            text.Append(Plain(line.Label)).Append(": ").Append(Money(line.Sales)).Append('\n');
        }

        text.Append(Bold($"TOTAL: {Money(vanTotal)}")).Append('\n');
        AppendOther(text, vanOther);

        if (shop is not null)
        {
            text.Append('\n');
            text.Append(Bold($"{Plain(shop.Heading)} ({date})")).Append('\n');
            foreach (var line in shop.Lines)
            {
                text.Append(Plain(line.Label)).Append(": ").Append(Quantity(line.Quantity)).Append('\n');
            }

            text.Append(Bold($"Total: {Money(shop.Total)}")).Append('\n');
            AppendOther(text, shop.OtherCurrencies);
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>"Wednesday 28.10.2026", as the office's sheet dates it.</summary>
    public static string DayLabel(DateTime day) => day.ToString("dddd dd.MM.yyyy", Invariant);

    public static string Money(decimal amount) =>
        (amount < 0 ? "-$" : "$") + Math.Abs(amount).ToString("#,0.00", Invariant);

    public static string Quantity(decimal quantity) => quantity.ToString("#,0.##", Invariant);

    private static void AppendOther(StringBuilder text, IReadOnlyList<DailySalesWhatsAppCurrencyAmount> other)
    {
        if (other.Count == 0)
        {
            return;
        }

        var amounts = string.Join(", ", other.Select(amount => $"{amount.Currency} {amount.Amount.ToString("#,0.00", Invariant)}"));
        text.Append(Italic($"Plus {amounts}, not in the total")).Append('\n');
    }

    private static string Bold(string text) => $"*{text}*";

    private static string Italic(string text) => $"_{text}_";

    /// <summary>
    /// A label with WhatsApp's formatting marks taken out. A "*" or "_" in a product name would otherwise
    /// open bold or italics that run on into the next line.
    /// </summary>
    private static string Plain(string label) =>
        label.Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("_", " ", StringComparison.Ordinal)
            .Replace("~", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Trim();
}
