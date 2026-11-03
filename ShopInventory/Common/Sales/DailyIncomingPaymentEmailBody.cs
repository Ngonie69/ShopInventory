using System.Globalization;
using System.Net;
using System.Text;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// One invoice as the cash-on-the-ground email and its workbook list it.
/// </summary>
/// <param name="InvoiceDocNum">The SAP invoice number, or null when only its DocEntry is known.</param>
/// <param name="InvoiceDocEntry">The SAP invoice's DocEntry.</param>
/// <param name="SaleReference">The till, van or consolidation reference the invoice came from.</param>
/// <param name="Cash">Cash applied to the invoice.</param>
/// <param name="Electronic">Ecocash, Innbucks and swipe money applied to the invoice.</param>
/// <param name="InvoiceDate">The day of the sale behind the invoice, when the sale is known.</param>
/// <param name="Source">Where the sale was made: a shop till, vending, a van, a consolidated day.</param>
public sealed record DailyPaymentEmailLine(
    int? InvoiceDocNum,
    int InvoiceDocEntry,
    string? SaleReference,
    decimal Cash,
    decimal Electronic,
    DateTime? InvoiceDate = null,
    string? Source = null)
{
    public decimal Total => Cash + Electronic;
}

/// <summary>
/// The email sent to the people who receive a business partner's cash on the ground, once the day's
/// incoming payment has posted. Every invoice it settles goes with it as a workbook
/// (<see cref="DailyIncomingPaymentWorkbook"/>).
/// </summary>
/// <remarks>
/// Its job is reconciliation at the counter: how much cash should be in hand, how much should have arrived
/// electronically, which accounts SAP booked them to, and the reference to quote to finance. The cash to
/// hand in is the largest figure on it for that reason.
///
/// <para>
/// Mail clients ignore most of CSS (Outlook renders with Word), so the layout is nested tables with
/// inline styles: no flex, no grid, no classes, no web fonts.
/// </para>
/// </remarks>
public static class DailyIncomingPaymentEmailBody
{
    /// <summary>How many invoices the email itself lists. The workbook has every one.</summary>
    public const int ListedInvoices = 8;

    private static readonly CultureInfo Money = CultureInfo.InvariantCulture;

    private const string Font = "font-family:'Segoe UI',Arial,Helvetica,sans-serif";
    private const string Ink = "#1b1c26";
    private const string Ink2 = "#35374a";
    private const string Mute = "#4e5164";
    private const string Mute2 = "#656879";
    private const string Rule = "#ececf2";
    private const string Edge = "#d4d6e2";

    /// <summary>
    /// The partner's name, never its code. A payment claimed from sales that carried no name has none of
    /// its own, so the name it was mapped under stands in; the code only when neither holds one.
    /// </summary>
    public static string PartnerName(DailyIncomingPaymentEntity payment, string? mappedName) =>
        NameOrNull(payment.CardName, payment.CardCode)
        ?? NameOrNull(mappedName, payment.CardCode)
        ?? payment.CardCode;

    public static string Subject(DailyIncomingPaymentEntity payment, string partnerName, IReadOnlyList<DailyPaymentEmailLine> lines) =>
        $"Daily takings posted: {partnerName}, {payment.PaymentDate.ToString("ddd dd MMM yyyy", Money)} · {Amount(lines.Sum(line => line.Total))} ({payment.Reference})";

    public static string Render(DailyIncomingPaymentEntity payment, string partnerName, IReadOnlyList<DailyPaymentEmailLine> lines)
    {
        var cash = lines.Sum(line => line.Cash);
        var electronic = lines.Sum(line => line.Electronic);
        var attachment = DailyIncomingPaymentWorkbook.FileName(payment, partnerName);

        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f4f8;{Font}\">");
        html.Append("<tr><td align=\"center\" style=\"padding:24px 12px\">");
        html.Append(CultureInfo.InvariantCulture, $"<table role=\"presentation\" width=\"640\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;max-width:640px;background:#ffffff;border:1px solid #dcdde6;border-radius:10px;border-collapse:separate;{Font};color:{Ink}\">");

        // Who and when.
        html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"padding:28px 32px 22px;border-bottom:1px solid {Rule}\">");
        html.Append("<div style=\"font-size:11px;font-weight:700;letter-spacing:1px;text-transform:uppercase;color:#5f52c4\">Daily takings &middot; posted to SAP</div>");
        html.Append(CultureInfo.InvariantCulture, $"<div style=\"margin:10px 0 6px;font-size:22px;line-height:28px;font-weight:700;color:{Ink}\">{E(partnerName)}</div>");
        html.Append(CultureInfo.InvariantCulture, $"<div style=\"font-size:14px;line-height:20px;color:{Mute}\">{payment.PaymentDate.ToString("dddd d MMMM yyyy", Money)} &middot; {Count(lines.Count, "invoice")} settled</div>");
        html.Append("</td></tr>");

        // The cash to hand in, largest on the page.
        html.Append("<tr><td style=\"padding:24px 32px 8px\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f4f8;border-radius:8px\"><tr>");
        html.Append(CultureInfo.InvariantCulture, $"<td style=\"padding:18px 20px\"><div style=\"font-size:14px;font-weight:600;color:{Ink2}\">Cash to hand in</div><div style=\"font-size:13px;color:{Mute2};margin-top:3px\">Booked to G/L {E(payment.CashAccount ?? "-")}</div></td>");
        html.Append(CultureInfo.InvariantCulture, $"<td align=\"right\" style=\"padding:18px 20px;font-size:32px;line-height:36px;font-weight:700;color:{Ink};white-space:nowrap\">{Amount(cash)}</td>");
        html.Append("</tr></table></td></tr>");

        // Electronic and the total.
        html.Append("<tr><td style=\"padding:4px 32px 0\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">");
        html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"padding:12px 0;border-bottom:1px solid {Rule};font-size:14px;color:{Ink2}\">Ecocash, Innbucks and swipe<div style=\"font-size:13px;color:{Mute2};margin-top:2px\">Booked to G/L {E(payment.TransferAccount ?? "-")}</div></td>");
        html.Append(CultureInfo.InvariantCulture, $"<td align=\"right\" style=\"padding:12px 0;border-bottom:1px solid {Rule};font-size:16px;font-weight:600;white-space:nowrap\">{Amount(electronic)}</td></tr>");
        html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"padding:12px 0;font-size:14px;font-weight:700\">Total settled</td><td align=\"right\" style=\"padding:12px 0;font-size:16px;font-weight:700;white-space:nowrap\">{Amount(cash + electronic)}</td></tr>");
        html.Append("</table></td></tr>");

        // The reference to quote, and SAP's own number.
        html.Append("<tr><td style=\"padding:12px 32px 0\">");
        html.Append(CultureInfo.InvariantCulture, $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border:1px solid {Edge};border-radius:8px;border-collapse:separate\"><tr>");
        html.Append(CultureInfo.InvariantCulture, $"<td style=\"padding:14px 18px;border-right:1px solid {Edge}\">{Label("Quote to finance")}<div style=\"margin-top:4px;font-size:15px;font-weight:700;font-family:Consolas,'Courier New',monospace;color:{Ink}\">{E(payment.Reference)}</div></td>");
        html.Append(CultureInfo.InvariantCulture, $"<td width=\"170\" style=\"padding:14px 18px\">{Label("SAP payment")}<div style=\"margin-top:4px;font-size:15px;font-weight:700;color:{Ink}\">{payment.SapDocNum?.ToString(Money) ?? "-"}</div></td>");
        html.Append("</tr></table></td></tr>");

        // The first invoices; the workbook has them all.
        html.Append("<tr><td style=\"padding:28px 32px 0\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        html.Append(CultureInfo.InvariantCulture, $"<td style=\"font-size:15px;font-weight:700;color:{Ink};padding-bottom:8px\">Invoices settled</td>");
        html.Append(CultureInfo.InvariantCulture, $"<td align=\"right\" style=\"font-size:12px;color:{Mute2};padding-bottom:8px\">{(lines.Count > ListedInvoices ? $"First {ListedInvoices} of {lines.Count} &middot; all in the attached workbook" : "Also in the attached workbook")}</td>");
        html.Append("</tr></table>");

        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse\">");
        html.Append("<tr>");
        html.Append(Heading("Invoice", "left", "72"));
        html.Append(Heading("Sale", "left", null));
        html.Append(Heading("Cash", "right", "84"));
        html.Append(Heading("Electronic", "right", "92"));
        html.Append(Heading("Total", "right", "84"));
        html.Append("</tr>");

        foreach (var line in lines.Take(ListedInvoices))
        {
            html.Append("<tr>");
            html.Append(Cell(line.InvoiceDocNum?.ToString(Money) ?? $"entry {line.InvoiceDocEntry.ToString(Money)}", "left"));
            html.Append(CultureInfo.InvariantCulture, $"<td style=\"padding:9px 10px;border-bottom:1px solid {Rule};font-size:12px;font-family:Consolas,'Courier New',monospace;color:{Mute};word-break:break-all\">{E(line.SaleReference ?? "-")}</td>");
            html.Append(Cell(Amount(line.Cash), "right"));
            html.Append(line.Electronic == 0 ? Cell("&ndash;", "right", Mute2, encoded: true) : Cell(Amount(line.Electronic), "right"));
            html.Append(Cell(Amount(line.Total), "right"));
            html.Append("</tr>");
        }

        if (lines.Count > ListedInvoices)
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td colspan=\"5\" style=\"padding:11px 10px;background:#f8f8fb;border-bottom:1px solid {Rule};font-size:13px;color:{Mute2}\">+ {Count(lines.Count - ListedInvoices, "more invoice")} in the attached workbook</td></tr>");
        }

        html.Append("<tr>");
        html.Append(CultureInfo.InvariantCulture, $"<td colspan=\"2\" style=\"padding:12px 10px;border-top:1px solid {Ink};font-size:14px;font-weight:700\">All {Count(lines.Count, "invoice")}</td>");
        foreach (var total in new[] { cash, electronic, cash + electronic })
        {
            html.Append(CultureInfo.InvariantCulture, $"<td align=\"right\" style=\"padding:12px 10px;border-top:1px solid {Ink};font-size:14px;font-weight:700;white-space:nowrap\">{Amount(total)}</td>");
        }

        html.Append("</tr></table></td></tr>");

        // What to do when the count is off.
        html.Append("<tr><td style=\"padding:28px 32px 0\">");
        html.Append(CultureInfo.InvariantCulture, $"<div style=\"padding:14px 18px;background:#fdf6e6;border:1px solid #e5cd94;border-radius:8px;font-size:14px;line-height:21px;color:#6b4c12\"><strong>Cash in hand doesn't match?</strong> Count again against the attached sheet, then send the difference to finance quoting <strong>{E(payment.Reference)}</strong>.</div>");
        html.Append("</td></tr>");

        html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"padding:22px 32px 26px;font-size:12px;line-height:18px;color:{Mute2}\">Attached: {E(attachment)}<br>Sent automatically by Shop Inventory once SAP accepted the payment. You get this because you are listed for {E(partnerName)} under Daily incoming payments &rsaquo; G/L accounts.</td></tr>");

        html.Append("</table></td></tr></table>");

        return html.ToString();
    }

    private static string Label(string text) =>
        $"<div style=\"font-size:11px;font-weight:700;letter-spacing:.6px;text-transform:uppercase;color:{Mute2}\">{E(text)}</div>";

    private static string Heading(string text, string align, string? width) =>
        $"<th align=\"{align}\"{(width is null ? string.Empty : $" width=\"{width}\"")} style=\"padding:9px 10px;border-bottom:1px solid {Edge};font-size:11px;font-weight:600;letter-spacing:.6px;text-transform:uppercase;color:{Mute2};text-align:{align};white-space:nowrap\">{E(text)}</th>";

    private static string Cell(string text, string align, string color = Ink, bool encoded = false) =>
        $"<td align=\"{align}\" style=\"padding:9px 10px;border-bottom:1px solid {Rule};font-size:14px;color:{color};text-align:{align};white-space:nowrap\">{(encoded ? text : E(text))}</td>";

    private static string Count(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", Money)} {noun}s";

    private static string? NameOrNull(string? name, string cardCode)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var tidy = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.Equals(tidy, cardCode, StringComparison.OrdinalIgnoreCase) ? null : tidy;
    }

    internal static string Amount(decimal value) => value.ToString("N2", Money);

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
