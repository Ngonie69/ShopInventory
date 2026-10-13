using ShopInventory.Common.Sales;
using ShopInventory.Configuration;

namespace ShopInventory.Features.VanSalesReports.Queries;

/// <summary>
/// What an online van sale came to with its VAT, for anything that sets van money beside cash.
/// </summary>
/// <remarks>
/// <para>
/// A reservation holds what SAP is given: unit prices before tax. An offline sale holds what the
/// customer paid, because that is what its receipt was signed over. Added together as they are stored,
/// the two are different kinds of money, and every figure set beside cash reads short by the VAT on
/// the online half. In the week of 1 October 2026 every van sale was online, and the reports read
/// 13.4% under the takings the reps declared.
/// </para>
/// <para>
/// The rule, in order. A sale whose receipt row is signed takes the receipt's total, which is the
/// figure filed with ZIMRA. Any other sale is grossed up from its own reservation lines, each at its
/// VAT group's rate, with the rounding <c>VanSaleFiscalFirstPoster</c> writes a receipt row with, so
/// the two routes come to the same figure for the same basket. A reservation with no lines has
/// nothing to tax and stays net, marked as such.
/// </para>
/// </remarks>
public static class VanSaleMoney
{
    /// <summary>One reservation line as SAP was given it: the amount before tax and the line's own code.</summary>
    public sealed record NetLine(string ItemCode, decimal LineTotal, string? TaxCode);

    /// <summary>A sale's tax-inclusive total, the VAT inside it, and whether VAT could be worked out.</summary>
    public readonly record struct Amount(decimal Total, decimal? Vat, bool IncludesVat);

    /// <summary>A sale with no receipt total: its net lines, each taxed at its own code's rate.</summary>
    public static Amount GrossUp(
        decimal netTotal,
        IReadOnlyCollection<NetLine> lines,
        string? currency,
        IReadOnlyDictionary<string, string> vatGroups,
        TaxSettings tax)
    {
        if (lines.Count == 0)
        {
            return new Amount(netTotal, null, false);
        }

        var taxed = lines
            .Select(line => (
                NetAmount: Math.Round(line.LineTotal, 2, MidpointRounding.AwayFromZero),
                TaxCode: ItemVatGroups.TaxCodeFor(line.ItemCode, line.TaxCode, vatGroups, currency)))
            .ToList();

        var vat = tax.VatOnBasket(taxed);
        return new Amount(taxed.Sum(line => line.NetAmount) + vat, vat, true);
    }

    /// <summary>
    /// One net line with its VAT added, for the line grain. Rounded per line, so a sale's lines can
    /// differ from its receipt total by a cent: the receipt rounds once per rate, not once per line.
    /// </summary>
    public static decimal GrossLine(
        decimal netAmount,
        string itemCode,
        string? taxCode,
        string? currency,
        IReadOnlyDictionary<string, string> vatGroups,
        TaxSettings tax) =>
        Math.Round(
            netAmount * (1 + tax.RateFor(ItemVatGroups.TaxCodeFor(itemCode, taxCode, vatGroups, currency))),
            2,
            MidpointRounding.AwayFromZero);

    /// <summary>The currency a van document is in when it names none.</summary>
    public static string CurrencyOf(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "USD" : currency;
}
