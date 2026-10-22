using System.Globalization;

namespace ShopInventory.Web.Components;

/// <summary>
/// How every sales report writes SAP's booked gross profit: a real minus sign, a margin to one place,
/// and a word beside it so a loss is never told by colour alone.
/// </summary>
public static class GrossProfitFormat
{
    /// <summary>The class a figure is drawn in: <c>is-loss</c>, <c>is-profit</c> or <c>is-even</c>.</summary>
    public static string Tone(decimal value) => value < 0 ? "is-loss" : value > 0 ? "is-profit" : "is-even";

    /// <summary>Loss, Profit or Break-even.</summary>
    public static string Verdict(decimal margin) => margin < 0 ? "Loss" : margin > 0 ? "Profit" : "Break-even";

    /// <summary>Money to two places with a real minus sign.</summary>
    public static string Money(decimal value) =>
        (value < 0 ? "−" : "") + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>A margin to one place with a real minus sign.</summary>
    public static string Percent(decimal value) =>
        (value < 0 ? "−" : "") + Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// One line for a tooltip or readout, such as "GP 42.00 · 28.0% margin", or "GP not in SAP yet".
    /// </summary>
    public static string Line(decimal? grossProfit, decimal? marginPercent) =>
        grossProfit is not { } gp
            ? "GP not in SAP yet"
            : marginPercent is { } margin ? $"GP {Money(gp)} · {Percent(margin)} margin" : $"GP {Money(gp)}";

    /// <summary>
    /// What the profit was measured on when only some of a row's sales are in SAP, such as "on 12 of 15
    /// sales"; null when all of them are.
    /// </summary>
    public static string? PartOf(int costedSales, int sales) =>
        costedSales > 0 && costedSales < sales ? $"on {costedSales:N0} of {sales:N0} sales" : null;

    /// <summary>A van report's document money, one figure a currency.</summary>
    public static List<GrossProfitFigure> Of(IEnumerable<ShopInventory.Web.Models.VanSalesMoney> totals) =>
        totals.Select(total => new GrossProfitFigure(
                total.Currency, total.GrossProfit, total.MarginPercent, total.CostedDocumentCount, total.DocumentCount, "sales"))
            .ToList();

    /// <summary>A route report's totals, one figure a currency; <paramref name="noun"/> is what they count.</summary>
    public static List<GrossProfitFigure> Of(IEnumerable<ShopInventory.Web.Models.RouteCustomerSalesTotalsModel> totals, string noun = "sales") =>
        totals.Select(total => new GrossProfitFigure(
                total.Currency, total.GrossProfit, total.MarginPercent, total.CostedSaleCount, total.SaleCount, noun))
            .ToList();

    /// <summary>A van report's line money, one figure a currency.</summary>
    public static List<GrossProfitFigure> Of(IEnumerable<ShopInventory.Web.Models.VanSalesLineMoney> totals) =>
        totals.Select(total => new GrossProfitFigure(
                total.Currency, total.GrossProfit, total.MarginPercent, total.CostedLineCount, total.LineCount, "lines"))
            .ToList();
}

/// <summary>One currency's gross profit on a report row, for <c>GrossProfitByCurrencyCell</c>.</summary>
/// <param name="Costed">How many of the row's sales (or lines) were costed.</param>
/// <param name="Count">How many the row has.</param>
/// <param name="Noun">What they are: "sales" or "lines".</param>
public sealed record GrossProfitFigure(
    string Currency, decimal? GrossProfit, decimal? MarginPercent, int Costed, int Count, string Noun);
