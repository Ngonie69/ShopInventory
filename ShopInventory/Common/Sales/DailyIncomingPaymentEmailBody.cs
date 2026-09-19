using System.Globalization;
using System.Net;
using System.Text;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// One invoice as the cash-on-the-ground email lists it.
/// </summary>
/// <param name="InvoiceDocNum">The SAP invoice number, or null when only its DocEntry is known.</param>
/// <param name="InvoiceDocEntry">The SAP invoice's DocEntry.</param>
/// <param name="SaleReference">The till, van or consolidation reference the invoice came from.</param>
/// <param name="Cash">Cash applied to the invoice.</param>
/// <param name="Electronic">Ecocash, Innbucks and swipe money applied to the invoice.</param>
public sealed record DailyPaymentEmailLine(
    int? InvoiceDocNum,
    int InvoiceDocEntry,
    string? SaleReference,
    decimal Cash,
    decimal Electronic);

/// <summary>
/// The email sent to the people who receive a business partner's cash on the ground, once the day's
/// incoming payment has posted.
/// </summary>
/// <remarks>
/// Its job is reconciliation at the counter: how much cash should be in hand, how much should have arrived
/// electronically, which accounts SAP booked them to, and the reference to quote to finance.
/// </remarks>
public static class DailyIncomingPaymentEmailBody
{
    private static readonly CultureInfo Money = CultureInfo.InvariantCulture;

    public static string Subject(DailyIncomingPaymentEntity payment) =>
        $"Daily takings posted: {payment.CardName ?? payment.CardCode} {payment.PaymentDate:dd MMM yyyy} ({payment.Reference})";

    public static string Render(DailyIncomingPaymentEntity payment, IReadOnlyList<DailyPaymentEmailLine> lines)
    {
        var cash = lines.Sum(line => line.Cash);
        var electronic = lines.Sum(line => line.Electronic);

        var html = new StringBuilder();
        html.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2933\">");
        html.Append("<h2 style=\"margin:0 0 4px\">Daily takings posted to SAP</h2>");
        html.Append(CultureInfo.InvariantCulture, $"<p style=\"margin:0 0 16px;color:#52606d\">{E(payment.CardName ?? payment.CardCode)} ({E(payment.CardCode)}), {payment.PaymentDate:dddd d MMMM yyyy}</p>");

        html.Append("<table cellpadding=\"6\" style=\"border-collapse:collapse;margin-bottom:16px\">");
        Row(html, "Reference", $"<strong>{E(payment.Reference)}</strong>");
        Row(html, "SAP payment", payment.SapDocNum?.ToString(Money) ?? "-");
        Row(html, "Cash to hand in", $"<strong>{Amount(cash)}</strong> to {E(payment.CashAccount ?? "-")}");
        Row(html, "Ecocash, Innbucks and swipe", $"<strong>{Amount(electronic)}</strong> to {E(payment.TransferAccount ?? "-")}");
        Row(html, "Total", $"<strong>{Amount(cash + electronic)}</strong> across {lines.Count} invoice(s)");
        html.Append("</table>");

        html.Append("<table cellpadding=\"6\" style=\"border-collapse:collapse;width:100%;max-width:640px\">");
        html.Append("<thead><tr style=\"background:#f0f4f8;text-align:left\">");
        html.Append("<th>Invoice</th><th>Sale</th><th style=\"text-align:right\">Cash</th><th style=\"text-align:right\">Electronic</th>");
        html.Append("</tr></thead><tbody>");

        foreach (var line in lines)
        {
            html.Append("<tr style=\"border-top:1px solid #e4e7eb\">");
            html.Append(CultureInfo.InvariantCulture, $"<td>{(line.InvoiceDocNum?.ToString(Money) ?? $"entry {line.InvoiceDocEntry}")}</td>");
            html.Append(CultureInfo.InvariantCulture, $"<td>{E(line.SaleReference ?? "-")}</td>");
            html.Append(CultureInfo.InvariantCulture, $"<td style=\"text-align:right\">{Amount(line.Cash)}</td>");
            html.Append(CultureInfo.InvariantCulture, $"<td style=\"text-align:right\">{Amount(line.Electronic)}</td>");
            html.Append("</tr>");
        }

        html.Append("</tbody></table>");
        html.Append("<p style=\"margin-top:16px;color:#52606d\">If the cash in hand does not match, quote the reference above to finance.</p>");
        html.Append("</div>");

        return html.ToString();
    }

    private static void Row(StringBuilder html, string label, string valueHtml) =>
        html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"color:#52606d\">{E(label)}</td><td>{valueHtml}</td></tr>");

    private static string Amount(decimal value) => value.ToString("N2", Money);

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
