using System.Globalization;
using System.Text;

namespace ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppReport;

/// <summary>
/// The report as WhatsApp text: the two tables of the office's daily sheet, then every shop and every
/// vending business partner by tender — one "label: figure" line per row, headings and totals in bold.
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
        IReadOnlyList<DailySalesWhatsAppTenderAmount> vanTenders,
        IReadOnlyList<DailySalesWhatsAppCurrencyAmount> vanOther,
        DailySalesWhatsAppShop? shop,
        DailySalesWhatsAppCounters counters)
    {
        var date = DayLabel(day);
        var text = new StringBuilder();

        text.Append(Bold($"Van Daily Sales ({date})")).Append('\n');
        if (vanLines.Count == 0)
        {
            text.Append("No van sales recorded.").Append('\n');
        }

        // Each route's figure carries what was not paid in cash in brackets, so the reader sees which truck
        // took the EcoCash without a block of zeros under every route.
        foreach (var line in vanLines)
        {
            text.Append(Plain(line.Label)).Append(": ").Append(Money(line.Sales));
            var notCash = line.Tenders.Where(tender => tender.Tender != "Cash" && tender.Amount != 0m).ToList();
            if (notCash.Count > 0)
            {
                text.Append(" (")
                    .Append(string.Join(", ", notCash.Select(tender => $"{tender.Tender} {Money(tender.Amount)}")))
                    .Append(')');
            }

            text.Append('\n');
        }

        // The total by tender, as the shop blocks list it: the four named tenders always, a zero included,
        // and "Not recorded" only when some sale named none.
        text.Append(Bold($"TOTAL: {Money(vanTotal)}")).Append('\n');
        if (vanLines.Count > 0)
        {
            foreach (var tender in vanTenders.Where(tender =>
                         tender.Tender != GetDailySalesWhatsAppReportHandler.NotRecordedLabel || tender.Amount != 0m))
            {
                text.Append(tender.Tender).Append(": ").Append(Money(tender.Amount)).Append('\n');
            }
        }

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

        text.Append('\n');
        text.Append(Bold($"Shop & Vending Sales ({date})")).Append('\n');
        foreach (var counter in counters.Shops)
        {
            AppendCounter(text, counter.Label, counter);
            text.Append('\n');
        }

        // Vending one business partner at a time. With more than one, their sum follows on one line: the
        // tenders are already in each block and in the grand total below.
        foreach (var partner in counters.VendingPartners)
        {
            AppendCounter(text, $"{counters.Vending.Label} - {partner.Label}", partner);
            text.Append('\n');
        }

        if (counters.VendingPartners.Count != 1)
        {
            text.Append(Bold($"{Plain(counters.Vending.Label)} total: {Money(counters.Vending.Total)}")).Append('\n');
            AppendOther(text, counters.Vending.OtherCurrencies);
            text.Append('\n');
        }
        AppendCounter(text, $"{counters.Total.Label.ToUpperInvariant()} TOTAL", counters.Total);

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// A shop, vending or the total: its dollar takings in the heading, then one line per tender. A block
    /// with no sales says so in one line instead of four zeros.
    /// </summary>
    private static void AppendCounter(StringBuilder text, string label, DailySalesWhatsAppCounter counter)
    {
        text.Append(Bold($"{Plain(label)}: {Money(counter.Total)}")).Append('\n');
        if (counter.SalesCount == 0)
        {
            text.Append("No sales recorded.").Append('\n');
            return;
        }

        foreach (var tender in counter.Tenders)
        {
            text.Append(Plain(tender.Tender)).Append(": ").Append(Money(tender.Amount)).Append('\n');
        }

        AppendOther(text, counter.OtherCurrencies);
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
