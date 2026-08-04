using ShopInventory.Web.Models;
using System.Globalization;
using System.Net;
using System.Text;

namespace ShopInventory.Web.Services;

/// <summary>
/// Builds the printable "Mobile Order" document — the PDF a merchandiser order is
/// filed and signed off from.
///
/// Imported from the Nocturne design <c>Mobile Order SAP-80005.dc.html</c>, which is a
/// paged document rather than a screen: a light print palette drawn from the Nocturne
/// neutral/accent ramps (ground #f3f5fe, accent #5d5294), a running header and footer
/// that repeat on every sheet, and a rotated confidentiality watermark behind the body.
///
/// The page geometry follows the design's own <c>doc-page</c> component:
///   * <c>@page { margin: 0 }</c> leaves Chrome no margin box to draw its date/URL/page
///     furniture in, so the sheet carries the visual inset itself.
///   * The horizontal inset is the body's padding; the vertical inset cannot be, because
///     body padding is spent once on the first and last sheet only. It lives instead on
///     the spacer rows of a single-cell wrapper table — browsers repeat a
///     &lt;thead&gt;/&lt;tfoot&gt; on every printed page, so every sheet starts below the
///     running header and ends above the running footer.
///   * The running header, footer and watermark are <c>position: fixed</c>, which stamps
///     them onto each page. The wrapper table sits in its own stacking context above the
///     watermark so the body text stays legible over it.
///
/// The design leaves them out, but two things the page genuinely captures are kept and
/// drawn in its own vocabulary: the uploaded purchase-order attachments (a table when
/// there are any, the design's callout when there are none) and the capture coordinates.
/// </summary>
public static class MobileOrderDocumentBuilder
{
    private const string OrganisationName = "Kefalos Cheese (Pvt) Ltd";
    private const string SystemName = "Shop Inventory Management System";
    private const string FooterNote = "Confidential — internal distribution only";
    private const string Watermark = "CONFIDENTIAL";

    public static string Build(
        SalesOrderDto order,
        IReadOnlyCollection<DocumentAttachmentDto> attachments,
        string? attachmentError,
        string? purchaseOrderReference,
        DateTime generatedAtCat)
    {
        ArgumentNullException.ThrowIfNull(order);

        var currency = string.IsNullOrWhiteSpace(order.Currency) ? "USD" : order.Currency.Trim();
        var lines = order.Lines ?? new List<SalesOrderLineDto>();
        var lineCount = lines.Count;
        var itemCount = lines.Sum(line => line.Quantity);
        var poReference = string.IsNullOrWhiteSpace(purchaseOrderReference)
            ? order.OrderNumber
            : purchaseOrderReference;

        var body = new StringBuilder();

        AppendTitleBlock(body, order, currency, generatedAtCat);
        AppendStatBand(body, currency, lineCount, itemCount, order.SubTotal, order.DocTotal);
        AppendOrderSummary(body, order, currency, poReference);
        AppendPurchaseOrderSection(body, attachments, attachmentError, poReference);
        AppendOrderLines(body, lines, lineCount, itemCount);
        AppendTotals(body, currency, itemCount, order.SubTotal, order.TaxAmount, order.DocTotal);

        var documentTitle = $"Mobile Order {order.OrderNumber}";

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>{Html(documentTitle)} — {Html(OrganisationName)}</title>
            <link rel="preconnect" href="https://fonts.googleapis.com">
            <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
            <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600&display=swap" rel="stylesheet">
            <style>{DocumentStyles}</style>
            </head>
            <body>
            <div class="mod-watermark" aria-hidden="true"><span>{Html(Watermark)}</span></div>
            <table class="mod-frame" role="presentation">
              <thead><tr><th>
                <div class="mod-hdr-space">
                  <div class="mod-runhead">
                    <span class="mod-org">{Html(OrganisationName)}</span>
                    <span>{Html(SystemName)}</span>
                    <span class="mod-doc">{Html(documentTitle)}</span>
                  </div>
                </div>
              </th></tr></thead>
              <tbody><tr><td>
            {body}
              </td></tr></tbody>
              <tfoot><tr><td>
                <div class="mod-ftr-space">
                  <div class="mod-runfoot">
                    <span>{Html(FooterNote)}</span>
                    <span class="mod-gen">Generated {Html(FormatCatStamp(generatedAtCat))}</span>
                  </div>
                </div>
              </td></tr></tfoot>
            </table>
            </body>
            </html>
            """;
    }

    // ── Sections ────────────────────────────────────────────────────────────────

    private static void AppendTitleBlock(
        StringBuilder body,
        SalesOrderDto order,
        string currency,
        DateTime generatedAtCat)
    {
        var customerLine = string.IsNullOrWhiteSpace(order.CardName)
            ? order.CardCode
            : order.CardName;

        body.Append("<div class=\"mod-title\">");
        body.Append("<div>");
        body.Append("<div class=\"mod-eyebrow\">Mobile order</div>");
        body.Append($"<h1 class=\"mod-h1\">{Html(order.OrderNumber)}</h1>");
        body.Append($"<div class=\"mod-customer\">{Html(customerLine)}</div>");
        body.Append($"<div class=\"mod-customer-meta\">Customer {Html(order.CardCode)} · {Html(currency)}</div>");
        body.Append("</div>");
        body.Append("<div class=\"mod-title-aside\">");
        body.Append($"<span class=\"mod-pill{StatusPillModifier(order.Status)}\">{Html(StatusLabel(order.Status))}</span>");
        body.Append("<div class=\"mod-dates\">");
        body.Append($"<div>Report date <span class=\"mod-date\">{Html(generatedAtCat.ToString("dd MMM yyyy", CultureInfo.InvariantCulture))}</span></div>");
        body.Append($"<div>Delivery date <span class=\"mod-date\">{Html(FormatDate(order.DeliveryDate))}</span></div>");
        body.Append("</div>");
        body.Append("</div>");
        body.Append("</div>");
    }

    private static void AppendStatBand(
        StringBuilder body,
        string currency,
        int lineCount,
        decimal itemCount,
        decimal subTotal,
        decimal docTotal)
    {
        body.Append("<div class=\"mod-stats\">");
        AppendStat(body, "Order lines", lineCount.ToString("N0", CultureInfo.InvariantCulture), tabular: false);
        AppendStat(body, "Items", QuantityDisplay.Format(itemCount), tabular: true);
        AppendStat(body, "Subtotal", Money(subTotal), tabular: true);
        AppendStat(body, $"Total {currency}", Money(docTotal), tabular: true, accent: true);
        body.Append("</div>");
    }

    private static void AppendStat(
        StringBuilder body,
        string label,
        string value,
        bool tabular,
        bool accent = false)
    {
        body.Append($"<div class=\"mod-stat{(accent ? " mod-stat-accent" : string.Empty)}\">");
        body.Append($"<div class=\"mod-stat-label\">{Html(label)}</div>");
        body.Append($"<div class=\"mod-stat-value{(tabular ? " mod-num" : string.Empty)}\">{Html(value)}</div>");
        body.Append("</div>");
    }

    private static void AppendOrderSummary(
        StringBuilder body,
        SalesOrderDto order,
        string currency,
        string poReference)
    {
        body.Append("<h2 class=\"mod-h2\">Order summary</h2>");
        body.Append("<div class=\"mod-rule\"></div>");
        body.Append("<div class=\"mod-summary\">");

        body.Append("<div>");
        AppendRow(body, "Order number", Html(order.OrderNumber), tabular: true);
        AppendRow(body, "PO reference", Html(poReference), tabular: true);
        AppendRow(body, "SAP document", NotRecorded(order.SAPDocNum?.ToString(CultureInfo.InvariantCulture), "Not synced"), tabular: true);
        AppendRow(body, "SAP DocEntry", NotRecorded(order.SAPDocEntry?.ToString(CultureInfo.InvariantCulture), "Not synced"), tabular: true);
        AppendRow(body, "Currency", Html(currency));
        body.Append("</div>");

        body.Append("<div>");
        AppendRow(body, "Received from", Html(order.Source == SalesOrderSource.Mobile ? "Mobile app" : "Web"));
        AppendRow(body, "Received at", order.CreatedAt == default
            ? NotRecorded(null)
            : Html(FormatCatStamp(ToCat(order.CreatedAt))));
        AppendRow(body, "Created by", NotRecorded(order.CreatedByUserName));
        AppendRow(body, "Device", NotRecorded(order.DeviceInfo, "Not captured"));
        AppendRow(body, "Merchandiser notes", NotRecorded(order.MerchandiserNotes, "None"));

        if (order.Latitude.HasValue && order.Longitude.HasValue)
        {
            var latitude = order.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture);
            var longitude = order.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture);
            var mapsUrl = $"https://www.google.com/maps?q={latitude},{longitude}";
            AppendRow(
                body,
                "Capture location",
                $"<a class=\"mod-link\" href=\"{Html(mapsUrl)}\">{Html($"{latitude}, {longitude}")}</a>",
                tabular: true);
        }

        body.Append("</div>");
        body.Append("</div>");
    }

    private static void AppendPurchaseOrderSection(
        StringBuilder body,
        IReadOnlyCollection<DocumentAttachmentDto> attachments,
        string? attachmentError,
        string poReference)
    {
        if (!string.IsNullOrWhiteSpace(attachmentError))
        {
            AppendCallout(
                body,
                "Purchase order attachments could not be read.",
                attachmentError);
            return;
        }

        if (attachments.Count == 0)
        {
            AppendCallout(
                body,
                "Physical purchase order not attached.",
                $"No uploaded PO document was found for reference {poReference} — the order was captured in the mobile app only.");
            return;
        }

        var label = attachments.Count == 1 ? "1 file" : $"{attachments.Count:N0} files";
        body.Append($"<h2 class=\"mod-h2\">Physical purchase order <span class=\"mod-h2-tail\">— {Html(label)} for reference {Html(poReference)}</span></h2>");
        body.Append("<table class=\"mod-table\">");
        body.Append("<thead><tr>");
        body.Append("<th>File name</th>");
        body.Append("<th class=\"mod-w-size mod-r\">Size</th>");
        body.Append("<th class=\"mod-w-when\">Uploaded</th>");
        body.Append("<th class=\"mod-w-who\">Uploaded by</th>");
        body.Append("<th>Description</th>");
        body.Append("</tr></thead><tbody>");

        foreach (var attachment in attachments)
        {
            body.Append("<tr>");
            body.Append($"<td class=\"mod-code\">{Html(attachment.FileName)}</td>");
            body.Append($"<td class=\"mod-r mod-num mod-dim\">{Html(attachment.FileSizeFormatted)}</td>");
            body.Append($"<td class=\"mod-dim mod-when\">{Html(FormatCatStamp(ToCat(attachment.UploadedAt)))}</td>");
            body.Append($"<td>{NotRecorded(attachment.UploadedByUserName, "Unknown uploader")}</td>");
            body.Append($"<td>{NotRecorded(attachment.Description, "—")}</td>");
            body.Append("</tr>");
        }

        body.Append("</tbody></table>");
    }

    private static void AppendOrderLines(
        StringBuilder body,
        IReadOnlyCollection<SalesOrderLineDto> lines,
        int lineCount,
        decimal itemCount)
    {
        if (lines.Count == 0)
        {
            body.Append("<h2 class=\"mod-h2\">Order lines</h2>");
            AppendCallout(
                body,
                "No line items were returned for this mobile order.",
                "The order exists but carries no priced lines — check the mobile capture before it is posted to SAP.");
            return;
        }

        var tail = $"— {lineCount:N0} {(lineCount == 1 ? "line" : "lines")}, {QuantityDisplay.Format(itemCount)} {(itemCount == 1m ? "item" : "items")}";

        body.Append($"<h2 class=\"mod-h2\">Order lines <span class=\"mod-h2-tail\">{Html(tail)}</span></h2>");
        body.Append("<table class=\"mod-table mod-lines\">");
        body.Append("<thead><tr>");
        body.Append("<th class=\"mod-w-idx\">#</th>");
        body.Append("<th class=\"mod-w-item\">Item</th>");
        body.Append("<th>Description</th>");
        body.Append("<th class=\"mod-w-qty mod-r\">Qty</th>");
        body.Append("<th class=\"mod-w-uom\">UoM</th>");
        body.Append("<th class=\"mod-w-wh\">WH</th>");
        body.Append("<th class=\"mod-w-unit mod-r\">Unit</th>");
        body.Append("<th class=\"mod-w-line mod-r\">Line total</th>");
        body.Append("</tr></thead><tbody>");

        var index = 0;
        foreach (var line in lines)
        {
            index++;
            body.Append("<tr>");
            body.Append($"<td class=\"mod-idx\">{index.ToString(CultureInfo.InvariantCulture)}</td>");
            body.Append($"<td class=\"mod-code\">{Html(line.ItemCode)}</td>");
            body.Append($"<td>{NotRecorded(line.ItemDescription, "—")}</td>");
            body.Append($"<td class=\"mod-r\">{Html(QuantityDisplay.Format(line.Quantity, line.UoMCode))}</td>");
            body.Append($"<td class=\"mod-dim\">{NotRecorded(line.UoMCode, "—")}</td>");
            body.Append($"<td class=\"mod-dim\">{NotRecorded(line.WarehouseCode, "—")}</td>");
            body.Append($"<td class=\"mod-r mod-unit\">{Html(Money(line.UnitPrice))}</td>");
            body.Append($"<td class=\"mod-r mod-linetotal\">{Html(Money(line.LineTotal))}</td>");
            body.Append("</tr>");
        }

        body.Append("</tbody></table>");
    }

    private static void AppendTotals(
        StringBuilder body,
        string currency,
        decimal itemCount,
        decimal subTotal,
        decimal taxAmount,
        decimal docTotal)
    {
        var items = $"{QuantityDisplay.Format(itemCount)} {(itemCount == 1m ? "item" : "items")}";

        body.Append("<div class=\"mod-totals\"><div class=\"mod-totals-box\">");
        body.Append($"<div class=\"mod-total-row\"><span class=\"mod-k\">Item count</span><span class=\"mod-v\">{Html(items)}</span></div>");
        body.Append($"<div class=\"mod-total-row\"><span class=\"mod-k\">Subtotal</span><span class=\"mod-v\">{Html($"{currency} {Money(subTotal)}")}</span></div>");
        body.Append($"<div class=\"mod-total-row\"><span class=\"mod-k\">Tax</span><span class=\"mod-v\">{Html($"{currency} {Money(taxAmount)}")}</span></div>");
        body.Append("<div class=\"mod-total-due\">");
        body.Append("<span class=\"mod-total-due-label\">Total due</span>");
        body.Append($"<span class=\"mod-total-due-value\">{Html($"{currency} {Money(docTotal)}")}</span>");
        body.Append("</div>");
        body.Append("</div></div>");
    }

    private static void AppendCallout(StringBuilder body, string lead, string detail)
    {
        body.Append("<div class=\"mod-callout\">");
        body.Append("<span class=\"mod-callout-dot\"></span>");
        body.Append($"<div><span class=\"mod-callout-lead\">{Html(lead)}</span> {Html(detail)}</div>");
        body.Append("</div>");
    }

    private static void AppendRow(StringBuilder body, string label, string valueHtml, bool tabular = false)
    {
        body.Append("<div class=\"mod-kv\">");
        body.Append($"<span class=\"mod-k\">{Html(label)}</span>");
        body.Append($"<span class=\"mod-v{(tabular ? " mod-num" : string.Empty)}\">{valueHtml}</span>");
        body.Append("</div>");
    }

    // ── Formatting ──────────────────────────────────────────────────────────────

    /// <summary>The design's status pill is the accent outline; a cancelled or rejected
    /// order takes the neutral outline instead, so the state reads at a glance without
    /// introducing a hue the Nocturne print palette does not carry.</summary>
    private static string StatusPillModifier(SalesOrderStatus status) => status switch
    {
        SalesOrderStatus.Cancelled or SalesOrderStatus.Rejected => " mod-pill-quiet",
        _ => string.Empty
    };

    /// <summary>Splits the PascalCase enum name so the letter-spaced uppercase pill reads
    /// as "PARTIALLY FULFILLED" rather than one run of letters.</summary>
    private static string StatusLabel(SalesOrderStatus status)
    {
        var name = status.ToString();
        var label = new StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
                label.Append(' ');

            label.Append(name[i]);
        }

        return label.ToString();
    }

    private static string Money(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime? value) => value.HasValue
        ? value.Value.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
        : "Not set";

    private static DateTime ToCat(DateTime value) => IAuditService.ToCAT(
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string FormatCatStamp(DateTime catValue) =>
        $"{catValue.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)} CAT";

    /// <summary>Renders a missing value in the design's faint ink rather than dropping the row.</summary>
    private static string NotRecorded(string? value, string fallback = "Not recorded") =>
        string.IsNullOrWhiteSpace(value)
            ? $"<span class=\"mod-none\">{Html(fallback)}</span>"
            : Html(value);

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    // ── Styles ──────────────────────────────────────────────────────────────────

    private const string DocumentStyles = """
        :root {
          --mod-ground: #f3f5fe;      /* Nocturne neutral-100 */
          --mod-line: #cfd3e5;        /* neutral-300 */
          --mod-line-soft: #e4e7f5;   /* neutral-200 */
          --mod-faint: #9397ab;       /* neutral-500 */
          --mod-mute: #75798c;        /* neutral-600 */
          --mod-quiet: #595d6c;       /* neutral-700 */
          --mod-body: #3f424d;        /* neutral-800 */
          --mod-ink: #292b31;         /* neutral-900 */
          --mod-strong: #161826;      /* the system ground, used as the darkest ink here */
          --mod-accent: #5d5294;      /* accent-700 */
          --mod-accent-mid: #796cbf;  /* accent-600 */
          --mod-accent-wash: #e7e5fe; /* accent-200 */
          --mod-accent-deep: #2b2741; /* accent-900 */
          --mod-margin: 0.6in;
        }

        *, *::before, *::after { box-sizing: border-box; }

        html, body { margin: 0; padding: 0; }

        body {
          font-family: "Inter", "Segoe UI", system-ui, sans-serif;
          font-size: 15px;
          line-height: 1.5;
          color: var(--mod-ink);
          background: var(--mod-ground);
          padding: 0 var(--mod-margin);
          -webkit-print-color-adjust: exact;
          print-color-adjust: exact;
        }

        a { color: var(--mod-accent); text-underline-offset: 2px; }

        /* ── Page furniture ──────────────────────────────────────────────────── */

        /* The wrapper table's thead/tfoot spacers carry the vertical page inset:
           browsers repeat them on every printed sheet, which body padding cannot do.
           z-index lifts the whole document above the fixed watermark. */
        .mod-frame { width: 100%; border-collapse: collapse; position: relative; z-index: 1; }
        .mod-frame > thead > tr > th,
        .mod-frame > tbody > tr > td,
        .mod-frame > tfoot > tr > td { padding: 0; text-align: left; font-weight: inherit; }

        .mod-runhead {
          display: flex;
          align-items: baseline;
          gap: 16px;
          padding-bottom: 8px;
          border-bottom: 1px solid var(--mod-line);
          font-size: 10px;
          letter-spacing: 0.13em;
          text-transform: uppercase;
          color: var(--mod-mute);
        }
        .mod-runhead .mod-org { color: var(--mod-body); }
        .mod-runhead .mod-doc { margin-left: auto; color: var(--mod-accent); }

        .mod-runfoot {
          display: flex;
          align-items: baseline;
          gap: 16px;
          padding-top: 8px;
          border-top: 1px solid var(--mod-line);
          font-size: 9.5px;
          letter-spacing: 0.1em;
          text-transform: uppercase;
          color: var(--mod-mute);
        }
        .mod-runfoot .mod-gen { margin-left: auto; }

        .mod-watermark { display: none; }

        /* ── Title block ─────────────────────────────────────────────────────── */

        .mod-title { display: flex; align-items: flex-start; gap: 32px; padding-top: 22px; }
        .mod-eyebrow {
          font-size: 10px;
          letter-spacing: 0.16em;
          text-transform: uppercase;
          color: var(--mod-accent);
          margin-bottom: 10px;
        }
        .mod-h1 {
          margin: 0;
          font-weight: 500;
          font-size: 38px;
          line-height: 1.1;
          letter-spacing: -0.02em;
          color: var(--mod-strong);
        }
        .mod-customer { margin-top: 8px; font-size: 15px; color: var(--mod-body); }
        .mod-customer-meta { margin-top: 2px; font-size: 13px; color: var(--mod-mute); }

        .mod-title-aside { margin-left: auto; text-align: right; flex: none; }
        .mod-pill {
          display: inline-block;
          padding: 5px 12px;
          border: 1px solid var(--mod-accent);
          border-radius: 6px;
          font-size: 11px;
          letter-spacing: 0.12em;
          text-transform: uppercase;
          color: var(--mod-accent);
        }
        .mod-pill-quiet { border-color: var(--mod-mute); color: var(--mod-mute); }
        .mod-dates { margin-top: 14px; font-size: 13px; line-height: 1.6; color: var(--mod-quiet); }
        .mod-date { color: var(--mod-ink); }

        /* ── Stat band ───────────────────────────────────────────────────────── */

        /* The 1px grid gap over a neutral-300 ground paints the hairlines between cells. */
        .mod-stats {
          display: grid;
          grid-template-columns: repeat(4, 1fr);
          gap: 1px;
          margin-top: 26px;
          background: var(--mod-line);
          border-radius: 8px;
          overflow: hidden;
          break-inside: avoid;
        }
        .mod-stat { background: var(--mod-ground); padding: 14px 16px; }
        .mod-stat-label {
          font-size: 9.5px;
          letter-spacing: 0.13em;
          text-transform: uppercase;
          color: var(--mod-mute);
        }
        .mod-stat-value { font-weight: 500; font-size: 22px; color: var(--mod-strong); margin-top: 4px; }
        .mod-stat-accent { background: var(--mod-accent-wash); }
        .mod-stat-accent .mod-stat-label { color: var(--mod-accent); }
        .mod-stat-accent .mod-stat-value { color: var(--mod-accent-deep); }

        /* ── Sections ────────────────────────────────────────────────────────── */

        .mod-h2 {
          margin: 32px 0 0;
          font-weight: 500;
          font-size: 13px;
          letter-spacing: 0.14em;
          text-transform: uppercase;
          color: var(--mod-accent);
        }
        .mod-h2-tail { color: var(--mod-faint); letter-spacing: 0.08em; }
        .mod-rule { height: 1px; background: var(--mod-line); margin-top: 8px; }

        .mod-summary { display: grid; grid-template-columns: 1fr 1fr; column-gap: 40px; margin-top: 4px; }
        .mod-kv {
          display: flex;
          gap: 16px;
          padding: 9px 0;
          border-bottom: 1px solid var(--mod-line-soft);
          font-size: 13.5px;
          break-inside: avoid;
        }
        .mod-kv:last-child { border-bottom: 0; }
        .mod-k { color: var(--mod-mute); min-width: 126px; flex: none; }
        .mod-v { margin-left: auto; text-align: right; }
        .mod-num { font-variant-numeric: tabular-nums; }
        .mod-none { color: var(--mod-faint); }
        .mod-link { color: var(--mod-accent); }

        .mod-callout {
          display: flex;
          gap: 14px;
          align-items: flex-start;
          margin-top: 18px;
          padding: 12px 16px;
          background: var(--mod-line-soft);
          border-radius: 8px;
          font-size: 13px;
          line-height: 1.55;
          color: var(--mod-body);
          break-inside: avoid;
        }
        .mod-callout-dot {
          width: 6px;
          height: 6px;
          border-radius: 50%;
          background: var(--mod-accent-mid);
          margin-top: 7px;
          flex: none;
        }
        .mod-callout-lead { color: var(--mod-strong); }

        /* ── Tables ──────────────────────────────────────────────────────────── */

        .mod-table { width: 100%; border-collapse: collapse; margin-top: 10px; font-size: 13px; }
        .mod-table thead tr { background: var(--mod-accent-wash); }
        .mod-table th {
          text-align: left;
          padding: 8px 6px;
          font-weight: 500;
          font-size: 9.5px;
          letter-spacing: 0.11em;
          text-transform: uppercase;
          color: var(--mod-accent);
        }
        .mod-table td { padding: 7px 6px; border-bottom: 1px solid var(--mod-line-soft); }
        .mod-table tbody { font-variant-numeric: tabular-nums; }
        .mod-table tbody tr:last-child td { border-bottom-color: var(--mod-line); }

        .mod-r { text-align: right; }
        .mod-idx { color: var(--mod-faint); }
        .mod-code { color: var(--mod-strong); }
        .mod-dim { color: var(--mod-mute); }
        .mod-unit { color: var(--mod-body); }
        .mod-linetotal { color: var(--mod-strong); }

        .mod-w-idx { width: 28px; }
        .mod-w-item { width: 72px; }
        .mod-w-qty { width: 52px; }
        .mod-w-uom { width: 40px; }
        .mod-w-wh { width: 52px; }
        .mod-w-unit { width: 74px; }
        .mod-w-line { width: 82px; }
        .mod-w-size { width: 64px; }
        .mod-w-when { width: 152px; }
        .mod-w-who { width: 132px; }
        /* The width above is only a hint under auto table layout, and a wrapped
           "02 Aug 2026 09:15 / CAT" reads as two different facts. */
        .mod-when { white-space: nowrap; }

        /* ── Totals ──────────────────────────────────────────────────────────── */

        .mod-totals { display: flex; justify-content: flex-end; margin-top: 20px; break-inside: avoid; }
        .mod-totals-box { width: 296px; font-size: 13.5px; font-variant-numeric: tabular-nums; }
        .mod-total-row { display: flex; padding: 8px 0; border-bottom: 1px solid var(--mod-line-soft); }
        .mod-total-due {
          display: flex;
          align-items: baseline;
          padding: 12px 14px;
          margin-top: 8px;
          background: var(--mod-accent-wash);
          border-radius: 8px;
        }
        .mod-total-due-label {
          font-size: 10px;
          letter-spacing: 0.13em;
          text-transform: uppercase;
          color: var(--mod-accent);
        }
        .mod-total-due-value {
          margin-left: auto;
          font-weight: 500;
          font-size: 20px;
          color: var(--mod-accent-deep);
        }

        /* ── Print geometry ──────────────────────────────────────────────────── */

        @media print {
          /* margin: 0 leaves Chrome no margin box to draw its own date/URL/page-count
             furniture in; the sheet carries the visual inset itself. */
          @page { margin: 0; }

          html, body { height: auto; overflow: visible; }

          /* Sized to the page margin: at 0.6in it clears the running header (24px of
             content plus its 0.27in top inset) with room to spare on every sheet. */
          .mod-hdr-space, .mod-ftr-space { height: var(--mod-margin); }

          .mod-runhead {
            position: fixed;
            top: 0;
            left: 0;
            right: 0;
            margin: 0;
            padding: 0.27in var(--mod-margin) 8px;
          }
          .mod-runfoot {
            position: fixed;
            bottom: 0;
            left: 0;
            right: 0;
            margin: 0;
            padding: 8px var(--mod-margin) 0.27in;
          }

          .mod-watermark {
            display: flex;
            position: fixed;
            left: 0;
            right: 0;
            top: 40%;
            justify-content: center;
            pointer-events: none;
            z-index: 0;
          }
          .mod-watermark span {
            transform: rotate(-24deg);
            transform-origin: center;
            white-space: nowrap;
            font-weight: 600;
            font-size: 58px;
            letter-spacing: 0.1em;
            color: rgba(93, 82, 148, 0.08);
          }

          /* Fills are the design, not decoration — force them past the print dialog's
             "Background graphics" default. */
          * { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
          h1, h2 { break-after: avoid; }
          tr { break-inside: avoid; }
          p, li { orphans: 3; widows: 3; }
        }
        """;
}
