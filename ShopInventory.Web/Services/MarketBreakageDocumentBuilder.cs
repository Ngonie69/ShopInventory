using ShopInventory.Web.Models;
using System.Globalization;
using System.Net;
using System.Text;

namespace ShopInventory.Web.Services;

/// <summary>
/// Builds the printable return slip for one market breakage report: what came off the van, what the
/// office counted, and the SAP transfer that moved it into the returns warehouse. It travels with the
/// stock, so it ends in two signature lines — the rep handing it over and whoever receives it.
/// </summary>
/// <remarks>
/// Drawn in the Mobile Order document's print vocabulary and stylesheet
/// (<see cref="MobileOrderDocumentBuilder.DocumentStyles"/>), with only the slip's own pieces added,
/// so the two printed documents read as one family. The rep's figures are shown as reported and the
/// office's count beside them; a line nobody counted reads "Not counted", never zero.
/// </remarks>
public static class MarketBreakageDocumentBuilder
{
    private const string OrganisationName = "Kefalos Cheese (Pvt) Ltd";
    private const string SystemName = "Shop Inventory Management System";
    private const string FooterNote = "Internal — market breakage return";

    public static string Build(MarketBreakageDetailDto report, DateTime generatedAtCat)
    {
        ArgumentNullException.ThrowIfNull(report);

        var transferred = report.Status == MarketBreakageStatus.Transferred;
        var counted = report.Lines.Any(line => line.ConfirmedQuantity is not null);
        var reportedTotal = report.Lines.Sum(line => line.ReportedQuantity);
        var countedTotal = report.Lines.Sum(line => line.ConfirmedQuantity ?? 0m);
        var returns = string.IsNullOrWhiteSpace(report.ReturnsWarehouseCode) ? "Returns" : report.ReturnsWarehouseCode;

        var body = new StringBuilder();
        AppendTitleBlock(body, report, returns, generatedAtCat);
        AppendStatBand(body, report.Lines.Count, reportedTotal, countedTotal, counted, transferred);
        AppendSummary(body, report, returns);
        AppendRemarks(body, report);
        AppendLines(body, report.Lines);
        AppendSignatures(body, report, returns);

        var documentTitle = $"Breakage Report #{report.Id}";

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>{Html(documentTitle)} — {Html(OrganisationName)}</title>
            <link rel="preconnect" href="https://fonts.googleapis.com">
            <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
            <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600&display=swap" rel="stylesheet">
            <style>{MobileOrderDocumentBuilder.DocumentStyles}{SlipStyles}</style>
            </head>
            <body>
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
                    <span class="mod-gen">Printed {Html(FormatCatStamp(generatedAtCat))}</span>
                  </div>
                </div>
              </td></tr></tfoot>
            </table>
            </body>
            </html>
            """;
    }

    // ── Sections ────────────────────────────────────────────────────────────────

    private static void AppendTitleBlock(StringBuilder body, MarketBreakageDetailDto report, string returns, DateTime generatedAtCat)
    {
        var shop = report.CardName ?? report.CardCode;

        body.Append("<div class=\"mod-title\">");
        body.Append("<div>");
        body.Append("<div class=\"mod-eyebrow\">Market breakage return</div>");
        body.Append($"<h1 class=\"mod-h1\">Report #{report.Id.ToString(CultureInfo.InvariantCulture)}</h1>");
        body.Append($"<div class=\"mod-customer\">{Html(report.VanWarehouseCode)} → {Html(returns)}</div>");
        body.Append($"<div class=\"mod-customer-meta\">Reported by {Html(report.ReportedByName)}{(string.IsNullOrWhiteSpace(shop) ? "" : $" · {Html(shop)}")}</div>");
        body.Append("</div>");
        body.Append("<div class=\"mod-title-aside\">");
        body.Append($"<span class=\"mod-pill{(report.Status == MarketBreakageStatus.Transferred ? "" : " mod-pill-quiet")}\">{Html(StatusLabel(report.Status))}</span>");
        body.Append("<div class=\"mod-dates\">");
        body.Append($"<div>Reported <span class=\"mod-date\">{Html(FormatDate(report.CapturedAtUtc))}</span></div>");
        if (report.TransferredAtUtc is DateTime transferredAt)
            body.Append($"<div>Transferred <span class=\"mod-date\">{Html(FormatDate(transferredAt))}</span></div>");
        else
            body.Append($"<div>Printed <span class=\"mod-date\">{Html(generatedAtCat.ToString("dd MMM yyyy", CultureInfo.InvariantCulture))}</span></div>");
        body.Append("</div>");
        body.Append("</div>");
        body.Append("</div>");
    }

    private static void AppendStatBand(
        StringBuilder body, int lineCount, decimal reportedTotal, decimal countedTotal, bool counted, bool transferred)
    {
        body.Append("<div class=\"mod-stats\">");
        AppendStat(body, "Products", lineCount.ToString("N0", CultureInfo.InvariantCulture));
        AppendStat(body, "Units reported", QuantityDisplay.Format(reportedTotal));
        AppendStat(body, "Difference", counted ? DiffText(countedTotal - reportedTotal) : "—");
        AppendStat(body, transferred ? "Moved to returns" : "Units counted", counted ? QuantityDisplay.Format(countedTotal) : "—", accent: true);
        body.Append("</div>");
    }

    private static void AppendStat(StringBuilder body, string label, string value, bool accent = false)
    {
        body.Append($"<div class=\"mod-stat{(accent ? " mod-stat-accent" : string.Empty)}\">");
        body.Append($"<div class=\"mod-stat-label\">{Html(label)}</div>");
        body.Append($"<div class=\"mod-stat-value mod-num\">{Html(value)}</div>");
        body.Append("</div>");
    }

    private static void AppendSummary(StringBuilder body, MarketBreakageDetailDto report, string returns)
    {
        body.Append("<h2 class=\"mod-h2\">Transfer</h2>");
        body.Append("<div class=\"mod-rule\"></div>");
        body.Append("<div class=\"mod-summary\">");

        body.Append("<div>");
        AppendRow(body, "Report", Html($"#{report.Id}"), tabular: true);
        AppendRow(body, "SAP transfer", NotRecorded(report.SapDocNum?.ToString(CultureInfo.InvariantCulture), "Not posted"), tabular: true);
        AppendRow(body, "SAP DocEntry", NotRecorded(report.SapDocEntry?.ToString(CultureInfo.InvariantCulture), "Not posted"), tabular: true);
        AppendRow(body, "From warehouse", Html(report.VanWarehouseCode));
        AppendRow(body, "To warehouse", Html(returns));
        body.Append("</div>");

        body.Append("<div>");
        AppendRow(body, "Reported by", NotRecorded(report.ReportedByName));
        AppendRow(body, "Shop", NotRecorded(report.CardName ?? report.CardCode, "In transit"));
        AppendRow(body, "Reported at", Html(FormatCatStamp(ToCat(report.CapturedAtUtc))), tabular: true);
        AppendRow(body, "Confirmed by", NotRecorded(report.DecidedByName));
        AppendRow(body, "Transferred at", report.TransferredAtUtc is DateTime at
            ? Html(FormatCatStamp(ToCat(at)))
            : NotRecorded(null, "Not transferred"), tabular: true);
        body.Append("</div>");

        body.Append("</div>");
    }

    private static void AppendRemarks(StringBuilder body, MarketBreakageDetailDto report)
    {
        if (!string.IsNullOrWhiteSpace(report.Remarks))
            AppendCallout(body, $"{report.ReportedByName} wrote:", report.Remarks);
        if (!string.IsNullOrWhiteSpace(report.DecisionRemarks))
            AppendCallout(body, $"{report.DecidedByName ?? "The office"} wrote:", report.DecisionRemarks);
    }

    private static void AppendLines(StringBuilder body, IReadOnlyCollection<MarketBreakageLineDto> lines)
    {
        var tail = $"— {lines.Count:N0} {(lines.Count == 1 ? "product" : "products")}";

        body.Append($"<h2 class=\"mod-h2\">Products <span class=\"mod-h2-tail\">{Html(tail)}</span></h2>");
        body.Append("<table class=\"mod-table\">");
        body.Append("<thead><tr>");
        body.Append("<th class=\"mod-w-idx\">#</th>");
        body.Append("<th class=\"mod-w-item\">Item</th>");
        body.Append("<th>Description</th>");
        body.Append("<th class=\"mbd-w-reason\">Reason</th>");
        body.Append("<th class=\"mbd-w-qty mod-r\">Reported</th>");
        body.Append("<th class=\"mbd-w-qty mod-r\">Counted</th>");
        body.Append("<th class=\"mbd-w-qty mod-r\">Difference</th>");
        body.Append("</tr></thead><tbody>");

        var index = 0;
        foreach (var line in lines.OrderBy(line => line.LineNum))
        {
            index++;
            body.Append("<tr>");
            body.Append($"<td class=\"mod-idx\">{index.ToString(CultureInfo.InvariantCulture)}</td>");
            body.Append($"<td class=\"mod-code\">{Html(line.ItemCode)}</td>");
            body.Append($"<td>{NotRecorded(line.ItemDescription, "—")}</td>");
            body.Append($"<td class=\"mod-dim\">{NotRecorded(line.Reason, "—")}</td>");
            body.Append($"<td class=\"mod-r\">{Html(QuantityDisplay.Format(line.ReportedQuantity))}</td>");
            if (line.ConfirmedQuantity is decimal confirmed)
            {
                var diff = confirmed - line.ReportedQuantity;
                body.Append($"<td class=\"mod-r mod-linetotal\">{Html(QuantityDisplay.Format(confirmed))}</td>");
                body.Append($"<td class=\"mod-r{(diff == 0 ? " mod-dim" : " mbd-diff")}\">{Html(DiffText(diff))}</td>");
            }
            else
            {
                body.Append("<td class=\"mod-r mbd-nowrap\"><span class=\"mod-none\">Not counted</span></td>");
                body.Append("<td class=\"mod-r\"><span class=\"mod-none\">—</span></td>");
            }
            body.Append("</tr>");
        }

        body.Append("</tbody></table>");
    }

    private static void AppendSignatures(StringBuilder body, MarketBreakageDetailDto report, string returns)
    {
        body.Append("<div class=\"mbd-signs\">");
        AppendSignature(body, "Handed over by", report.ReportedByName);
        AppendSignature(body, $"Received into {returns} by", null);
        body.Append("</div>");
    }

    private static void AppendSignature(StringBuilder body, string label, string? name)
    {
        body.Append("<div class=\"mbd-sign\">");
        body.Append($"<div class=\"mbd-sign-label\">{Html(label)}</div>");
        body.Append("<div class=\"mbd-sign-line\"></div>");
        body.Append($"<div class=\"mbd-sign-meta\"><span>{(string.IsNullOrWhiteSpace(name) ? "Name &amp; signature" : $"{Html(name)} · signature")}</span><span>Date</span></div>");
        body.Append("</div>");
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

    /// <summary>The page's word for a status: a pending report is one the office has to count.</summary>
    private static string StatusLabel(string status)
        => status == MarketBreakageStatus.Pending ? "To count" : MarketBreakageStatus.Describe(status);

    /// <summary>Counted less reported, with its sign: "−2 short", "+1 over", "Matches".</summary>
    private static string DiffText(decimal diff) => diff switch
    {
        0 => "Matches",
        < 0 => $"−{QuantityDisplay.Format(-diff)} short",
        _ => $"+{QuantityDisplay.Format(diff)} over"
    };

    private static string FormatDate(DateTime utc) => ToCat(utc).ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

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

    /// <summary>Only what the slip adds to the shared document sheet: column widths and the signatures.</summary>
    private const string SlipStyles = """

        .mbd-w-reason { width: 96px; }
        .mbd-w-qty { width: 78px; }
        .mbd-diff { color: var(--mod-accent); font-weight: 500; white-space: nowrap; }
        .mbd-nowrap { white-space: nowrap; }

        .mbd-signs {
          display: grid;
          grid-template-columns: 1fr 1fr;
          column-gap: 48px;
          margin-top: 48px;
          break-inside: avoid;
        }
        .mbd-sign-label {
          font-size: 9.5px;
          letter-spacing: 0.13em;
          text-transform: uppercase;
          color: var(--mod-mute);
        }
        .mbd-sign-line {
          height: 44px;
          border-bottom: 1px solid var(--mod-quiet);
        }
        .mbd-sign-meta {
          display: flex;
          justify-content: space-between;
          margin-top: 6px;
          font-size: 11px;
          color: var(--mod-faint);
        }
        """;
}
