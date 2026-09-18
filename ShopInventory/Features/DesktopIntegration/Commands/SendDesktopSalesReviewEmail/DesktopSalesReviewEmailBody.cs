using System.Globalization;
using System.Net;
using System.Text;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;

namespace ShopInventory.Features.DesktopIntegration.Commands.SendDesktopSalesReviewEmail;

/// <summary>
/// The message the review is sent in: the headline and every finding, so the email is useful unopened,
/// with the full review attached as a PDF.
/// </summary>
/// <remarks>
/// Tables and inline styles only — the markup mail clients still render the same. Every value is
/// HTML-encoded: shop names, item descriptions and SAP's refusal text all come from data.
/// </remarks>
internal static class DesktopSalesReviewEmailBody
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Build(DesktopSalesReview review, string title, DateTime generatedAtCat)
    {
        var html = new StringBuilder();
        html.Append("<div style=\"font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#0f1a22;max-width:680px;margin:0 auto;\">");
        html.Append("<p style=\"font-size:11px;letter-spacing:1px;color:#1d5f8a;margin:0 0 4px;\"><b>DESKTOP SALES · BUSINESS REVIEW</b></p>");
        html.Append($"<h1 style=\"font-size:24px;margin:0 0 4px;\">{E(title)}</h1>");
        html.Append($"<p style=\"font-size:14px;color:#44525c;margin:0 0 16px;\">{D(review.FromDate, "ddd d MMM yyyy")} to {D(review.ToDate, "ddd d MMM yyyy")} · "
            + $"{(review.WarehouseCode is null ? "all shops" : E(review.WarehouseCode))} · compared with {D(review.PreviousFromDate, "d MMM")} to {D(review.PreviousToDate, "d MMM")}</p>");

        if (review.Currencies.Count == 0)
        {
            html.Append("<p style=\"font-size:14px;\">No sales were recorded in this period.</p>");
        }

        foreach (var section in review.Currencies)
        {
            Headline(html, section);
        }

        html.Append("<h2 style=\"font-size:17px;margin:20px 0 8px;\">What the figures say</h2>");
        if (review.Findings.Count == 0)
        {
            html.Append("<p style=\"font-size:14px;color:#44525c;\">Nothing stood out this period.</p>");
        }

        foreach (var finding in review.Findings)
        {
            var (label, color) = finding.Severity switch
            {
                DesktopSalesReviewSeverity.Action => ("ACT", "#c03131"),
                DesktopSalesReviewSeverity.Review => ("CHECK", "#b26f00"),
                _ => ("NOTE", "#1d5f8a")
            };

            html.Append($"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin:0 0 10px;border-left:3px solid {color};\"><tr>");
            html.Append($"<td style=\"padding:2px 10px;vertical-align:top;width:52px;font-size:11px;font-weight:bold;color:{color};\">{label}</td>");
            html.Append("<td style=\"padding:0 0 0 4px;\">");
            html.Append($"<div style=\"font-size:14px;font-weight:bold;\">{E(finding.Title)}</div>");
            html.Append($"<div style=\"font-size:13px;color:#44525c;line-height:1.45;\">{E(finding.Detail)}</div>");
            html.Append("</td></tr></table>");
        }

        html.Append("<p style=\"font-size:13px;color:#44525c;margin:20px 0 4px;\">The full review — days, hours, shops, best sellers, prices and posting — is attached as a PDF, "
            + "and can be opened for any period under Reports → Desktop Sales Review.</p>");
        html.Append($"<p style=\"font-size:11px;color:#84919a;margin:0;\">Generated {D(generatedAtCat, "d MMM yyyy, HH:mm")} CAT. Online van receipts are excluded; those sales are counted as their SAP invoices.</p>");
        html.Append("</div>");

        return html.ToString();
    }

    private static void Headline(StringBuilder html, DesktopSalesReviewCurrency section)
    {
        var c = section.Currency;
        var h = section.Headline;
        var tiles = new (string Label, string Value, string Detail)[]
        {
            ("Takings", Money(c, h.TotalAmount),
                h.TotalChangePercent is { } change ? $"{(change >= 0 ? "+" : "-")}{Math.Abs(change).ToString("0.#", Invariant)}% on the previous period" : "no previous period"),
            ("Sales", h.SalesCount.ToString("N0", Invariant), $"average {Money(c, h.AverageSale)}"),
            ("Before VAT", Money(c, h.NetAmount), $"VAT {Money(c, h.VatAmount)}"),
            ("Gross margin", h.MarginPercent is { } margin ? $"{margin.ToString("0.#", Invariant)}%" : "—",
                h.GrossProfit is { } profit ? Money(c, profit) : "not in SAP yet"),
        };

        html.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"4\" width=\"100%\" style=\"margin:0 0 8px;\"><tr>");
        foreach (var (label, value, detail) in tiles)
        {
            html.Append("<td style=\"background:#f3f6f8;padding:10px;vertical-align:top;width:25%;\">");
            html.Append($"<div style=\"font-size:10px;letter-spacing:1px;color:#7a8790;\">{E(label.ToUpperInvariant())}</div>");
            html.Append($"<div style=\"font-size:18px;font-weight:bold;margin:2px 0;\">{E(value)}</div>");
            html.Append($"<div style=\"font-size:12px;color:#44525c;\">{E(detail)}</div>");
            html.Append("</td>");
        }

        html.Append("</tr></table>");

        var shops = section.ByShop.Take(8).ToList();
        if (shops.Count > 1)
        {
            html.Append("<table role=\"presentation\" cellpadding=\"4\" cellspacing=\"0\" width=\"100%\" style=\"font-size:13px;border-collapse:collapse;margin:0 0 12px;\">");
            html.Append("<tr style=\"color:#7a8790;font-size:11px;\"><td>SHOP</td><td align=\"right\">DAYS</td><td align=\"right\">TAKINGS</td><td align=\"right\">PER DAY</td><td align=\"right\">SHARE</td></tr>");
            foreach (var shop in shops)
            {
                html.Append("<tr style=\"border-top:1px solid #dde4e8;\">");
                html.Append($"<td>{E(shop.Label)}<span style=\"color:#7a8790;\"> · {E(shop.Channel)}</span></td>");
                html.Append($"<td align=\"right\">{shop.DaysTraded}</td>");
                html.Append($"<td align=\"right\">{shop.TotalAmount.ToString("N2", Invariant)}</td>");
                html.Append($"<td align=\"right\">{shop.PerTradingDay.ToString("N2", Invariant)}</td>");
                html.Append($"<td align=\"right\">{shop.ShareOfValuePercent.ToString("0.#", Invariant)}%</td>");
                html.Append("</tr>");
            }

            html.Append("</table>");
        }
    }

    private static string Money(string currency, decimal amount) => $"{currency} {amount.ToString("N2", Invariant)}";

    private static string D(DateTime date, string format) => date.ToString(format, Invariant);

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
