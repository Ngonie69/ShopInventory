using ShopInventory.Web.Models;
using System.Globalization;
using System.Net;
using System.Text;

namespace ShopInventory.Web.Services;

/// <summary>
/// Builds the printable transfer note for one factory transfer: what left the source, where it is
/// held, what the receiving side counted, and the two SAP legs that moved it. It travels with the
/// stock, so it ends in signature lines for whoever sent it, the driver on a depot truck, and whoever
/// receives it.
/// </summary>
/// <remarks>
/// Drawn in the Mobile Order document's print vocabulary and stylesheet
/// (<see cref="MobileOrderDocumentBuilder.DocumentStyles"/>), as the breakage return slip is, so the
/// printed documents read as one family.
/// <para>
/// Until a transfer is settled it prints as a draft: "DRAFT" is stamped across every sheet and named
/// in the running header and footer, and the counts carry whatever the receiver has typed into the
/// drawer so far, so the paper matches the screen. A transfer that has not been sent at all prints
/// the same way from the send form (<see cref="BuildUnsent"/>), as a loading or release sheet.
/// </para>
/// </remarks>
public static class FactoryTransferDocumentBuilder
{
    private const string OrganisationName = "Kefalos Cheese (Pvt) Ltd";
    private const string SystemName = "Shop Inventory Management System";
    private const string FooterNote = "Internal — factory transfer note";
    private const string CancelledFooterNote = "Cancelled before it reached SAP — nothing moved";
    private const string UnsentFooterNote = "Draft — not sent; no stock has moved";
    private const string DraftWatermark = "DRAFT";

    /// <summary>A transfer that is not yet settled prints as a draft.</summary>
    public static bool IsDraft(string status) => !FactoryTransferStatus.IsSettled(status);

    /// <param name="transfer">The transfer as the drawer holds it.</param>
    /// <param name="generatedAtCat">When it is printed, in CAT.</param>
    /// <param name="draftCounts">
    /// For a draft only: the counts typed into the drawer and not yet submitted, by line id. A line
    /// missing from it prints as not counted. Ignored once the transfer is settled, when the accepted
    /// quantities are the record.
    /// </param>
    public static string Build(
        FactoryTransferDetailDto transfer,
        DateTime generatedAtCat,
        IReadOnlyDictionary<int, decimal>? draftCounts = null)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        return Render(transfer, generatedAtCat, draftCounts, unsent: false);
    }

    /// <summary>
    /// A transfer still being filled in on the send form, with no number and nothing posted.
    /// Only the summary's route, department, direction, truck and reason, and the lines, are read.
    /// </summary>
    public static string BuildUnsent(FactoryTransferDetailDto transfer, DateTime generatedAtCat)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        return Render(transfer, generatedAtCat, draftCounts: null, unsent: true);
    }

    private static string Render(
        FactoryTransferDetailDto transfer,
        DateTime generatedAtCat,
        IReadOnlyDictionary<int, decimal>? draftCounts,
        bool unsent)
    {
        var summary = transfer.Summary;
        var draft = unsent || IsDraft(summary.Status);

        // As the API posts it: a rejected or recalled transfer accepts nothing, whatever was counted.
        decimal? CountOf(FactoryTransferLineDto line)
        {
            if (unsent)
                return null;
            if (draft)
                return draftCounts is not null && draftCounts.TryGetValue(line.Id, out var typed) ? typed : null;
            return summary.Status switch
            {
                FactoryTransferStatus.Rejected or FactoryTransferStatus.Recalled => 0m,
                FactoryTransferStatus.Cancelled => null,
                _ => line.AcceptedQuantity ?? 0m
            };
        }

        var lines = transfer.Lines.OrderBy(line => line.LineNum).ToList();
        var counted = lines.Any(line => CountOf(line) is not null);
        var allCounted = lines.Count > 0 && lines.All(line => CountOf(line) is not null);
        var sentTotal = lines.Sum(line => line.Quantity);
        var countedTotal = lines.Sum(line => CountOf(line) ?? 0m);
        var delivery = summary.Direction == FactoryTransferStatus.Delivery;
        var reference = unsent ? "New transfer" : DisplayNumber(summary);

        var body = new StringBuilder();
        AppendTitleBlock(body, summary, reference, unsent, generatedAtCat);
        AppendStatBand(body, lines.Count, sentTotal, countedTotal, counted, allCounted, draft, unsent);
        AppendSummary(body, transfer, reference, unsent);
        AppendRemarks(body, transfer, unsent);
        AppendLines(body, lines, CountOf, delivery, draft, unsent);
        AppendSignatures(body, summary, delivery, unsent);

        var documentTitle = unsent ? "New Factory Transfer — Draft"
            : draft ? $"Factory Transfer {reference} — Draft"
            : $"Factory Transfer {reference}";
        var footer = unsent ? UnsentFooterNote
            : draft ? DraftFooter(summary)
            : summary.Status == FactoryTransferStatus.Cancelled ? CancelledFooterNote
            : FooterNote;
        var watermark = draft
            ? $"<div class=\"mod-watermark ftd-watermark-draft\" aria-hidden=\"true\"><span>{DraftWatermark}</span></div>"
            : string.Empty;

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>{Html(documentTitle)} — {Html(OrganisationName)}</title>
            <link rel="preconnect" href="https://fonts.googleapis.com">
            <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
            <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600&display=swap" rel="stylesheet">
            <style>{MobileOrderDocumentBuilder.DocumentStyles}{NoteStyles}</style>
            </head>
            <body>
            {watermark}
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
                    <span>{Html(footer)}</span>
                    <span class="mod-gen">Printed {Html(FormatCatStamp(generatedAtCat))}</span>
                  </div>
                </div>
              </td></tr></tfoot>
            </table>
            </body>
            </html>
            """;
    }

    /// <summary>What a draft is still waiting on, for the running footer.</summary>
    private static string DraftFooter(FactoryTransferSummaryDto summary) => summary.Status switch
    {
        FactoryTransferStatus.Sending or FactoryTransferStatus.SendFailed => "Draft — not yet posted to SAP; no stock has moved",
        _ => $"Draft — not yet accepted into {summary.DestinationWarehouseCode}"
    };

    // ── Sections ────────────────────────────────────────────────────────────────

    private static void AppendTitleBlock(
        StringBuilder body, FactoryTransferSummaryDto summary, string reference, bool unsent, DateTime generatedAtCat)
    {
        var settledWell = summary.Status is FactoryTransferStatus.Accepted or FactoryTransferStatus.PartiallyAccepted;
        var status = unsent ? "Not sent" : FactoryTransferStatus.Describe(summary.Status);

        body.Append("<div class=\"mod-title\">");
        body.Append("<div>");
        body.Append($"<div class=\"mod-eyebrow\">{Html(Eyebrow(summary))}</div>");
        body.Append($"<h1 class=\"mod-h1\">{Html(reference)}</h1>");
        body.Append($"<div class=\"mod-customer\">{Html(RouteText(summary))}</div>");
        body.Append($"<div class=\"mod-customer-meta\">{Html(unsent ? "Not sent yet" : $"Sent by {summary.SentByName}")}</div>");
        body.Append("</div>");
        body.Append("<div class=\"mod-title-aside\">");
        body.Append($"<span class=\"mod-pill{(settledWell ? "" : " mod-pill-quiet")}\">{Html(status)}</span>");
        body.Append("<div class=\"mod-dates\">");
        if (!unsent)
            body.Append($"<div>Sent <span class=\"mod-date\">{Html(FormatDate(summary.SentAtUtc))}</span></div>");
        if (!unsent && summary.DecidedAtUtc is DateTime decidedAt)
            body.Append($"<div>{Html(DecidedWord(summary))} <span class=\"mod-date\">{Html(FormatDate(decidedAt))}</span></div>");
        else
            body.Append($"<div>Printed <span class=\"mod-date\">{Html(generatedAtCat.ToString("dd MMM yyyy", CultureInfo.InvariantCulture))}</span></div>");
        body.Append("</div>");
        body.Append("</div>");
        body.Append("</div>");
    }

    private static void AppendStatBand(
        StringBuilder body, int lineCount, decimal sentTotal, decimal countedTotal, bool counted, bool allCounted, bool draft, bool unsent)
    {
        // As in the drawer, a part-counted draft has no difference yet: the uncounted lines are not zero.
        body.Append("<div class=\"mod-stats\">");
        AppendStat(body, "Products", lineCount.ToString("N0", CultureInfo.InvariantCulture));
        AppendStat(body, unsent ? "Units to send" : "Units sent", QuantityDisplay.Format(sentTotal));
        AppendStat(body, "Difference", allCounted ? DiffText(countedTotal - sentTotal, draft) : "—");
        AppendStat(body, draft ? "Units counted" : "Units accepted", counted ? QuantityDisplay.Format(countedTotal) : "—", accent: true);
        body.Append("</div>");
    }

    private static void AppendStat(StringBuilder body, string label, string value, bool accent = false)
    {
        body.Append($"<div class=\"mod-stat{(accent ? " mod-stat-accent" : string.Empty)}\">");
        body.Append($"<div class=\"mod-stat-label\">{Html(label)}</div>");
        body.Append($"<div class=\"mod-stat-value mod-num\">{Html(value)}</div>");
        body.Append("</div>");
    }

    private static void AppendSummary(StringBuilder body, FactoryTransferDetailDto transfer, string reference, bool unsent)
    {
        var summary = transfer.Summary;
        var notPosted = unsent ? "Not sent" : "Not posted";

        body.Append("<h2 class=\"mod-h2\">Transfer</h2>");
        body.Append("<div class=\"mod-rule\"></div>");
        body.Append("<div class=\"mod-summary\">");

        body.Append("<div>");
        AppendRow(body, "Transfer", Html(reference), tabular: true);
        AppendRow(body, "From", NotRecorded(summary.SourceLabel));
        AppendRow(body, "Held in", NotRecorded(summary.HoldingWarehouseCode));
        AppendRow(body, "For", NotRecorded(summary.DestinationWarehouseCode));
        AppendRow(body, "SAP out", summary.OutSapDocNum > 0
            ? Html($"#{summary.OutSapDocNum.ToString(CultureInfo.InvariantCulture)}")
            : NotRecorded(null, notPosted), tabular: true);
        AppendRow(body, "SAP in", summary.InSapDocNum > 0
            ? Html($"#{summary.InSapDocNum.ToString(CultureInfo.InvariantCulture)}")
            : NotRecorded(null, notPosted), tabular: true);
        body.Append("</div>");

        body.Append("<div>");
        AppendRow(body, "Sent by", unsent ? NotRecorded(null, "Not sent") : NotRecorded(summary.SentByName));
        AppendRow(body, "Sent at", unsent
            ? NotRecorded(null, "Not sent")
            : Html(FormatCatStamp(ToCat(summary.SentAtUtc))), tabular: true);
        if (!string.IsNullOrWhiteSpace(summary.VehiclePlate) || summary.Direction == FactoryTransferStatus.Delivery)
        {
            AppendRow(body, "Truck", NotRecorded(summary.VehiclePlate));
            AppendRow(body, "Driver", NotRecorded(summary.DriverName));
        }
        if (summary.SourceRequestDocNum > 0)
            AppendRow(body, "Depot order", Html($"#{summary.SourceRequestDocNum.ToString(CultureInfo.InvariantCulture)}"), tabular: true);
        if (!unsent)
        {
            AppendRow(body, $"{DecidedWord(summary)} by", NotRecorded(summary.DecidedByName, "Not yet"));
            AppendRow(body, $"{DecidedWord(summary)} at", summary.DecidedAtUtc is DateTime at
                ? Html(FormatCatStamp(ToCat(at)))
                : NotRecorded(null, "Not yet"), tabular: true);
        }
        body.Append("</div>");

        body.Append("</div>");
    }

    private static void AppendRemarks(StringBuilder body, FactoryTransferDetailDto transfer, bool unsent)
    {
        var summary = transfer.Summary;
        if (!string.IsNullOrWhiteSpace(summary.Reason))
            AppendCallout(body, unsent || string.IsNullOrWhiteSpace(summary.SentByName) ? "Reason:" : $"{summary.SentByName} wrote:", summary.Reason);
        if (!unsent && !string.IsNullOrWhiteSpace(transfer.DecisionReason))
            AppendCallout(body, $"{(string.IsNullOrWhiteSpace(summary.DecidedByName) ? "The receiver" : summary.DecidedByName)} wrote:", transfer.DecisionReason);
    }

    private static void AppendLines(
        StringBuilder body,
        IReadOnlyCollection<FactoryTransferLineDto> lines,
        Func<FactoryTransferLineDto, decimal?> countOf,
        bool showSources,
        bool draft,
        bool unsent)
    {
        var tail = $"— {lines.Count:N0} {(lines.Count == 1 ? "product" : "products")}";

        body.Append($"<h2 class=\"mod-h2\">Products <span class=\"mod-h2-tail\">{Html(tail)}</span></h2>");
        body.Append("<table class=\"mod-table\">");
        body.Append("<thead><tr>");
        body.Append("<th class=\"mod-w-idx\">#</th>");
        body.Append("<th class=\"mod-w-item\">Item</th>");
        body.Append("<th>Description</th>");
        if (showSources)
            body.Append("<th class=\"ftd-w-from\">From</th>");
        body.Append("<th class=\"ftd-w-batch\">Batches</th>");
        body.Append($"<th class=\"ftd-w-qty mod-r\">{(unsent ? "To send" : "Sent")}</th>");
        body.Append($"<th class=\"ftd-w-qty mod-r\">{(draft ? "Counted" : "Accepted")}</th>");
        body.Append("<th class=\"ftd-w-qty mod-r\">Difference</th>");
        body.Append("</tr></thead><tbody>");

        var index = 0;
        foreach (var line in lines)
        {
            index++;
            body.Append("<tr>");
            body.Append($"<td class=\"mod-idx\">{index.ToString(CultureInfo.InvariantCulture)}</td>");
            body.Append($"<td class=\"mod-code\">{Html(line.ItemCode)}</td>");
            body.Append($"<td>{NotRecorded(line.ItemDescription, "—")}{(string.IsNullOrWhiteSpace(line.UoMCode) ? "" : $"<div class=\"mod-dim\">{Html(line.UoMCode)}</div>")}</td>");
            if (showSources)
                body.Append($"<td class=\"mod-code\">{NotRecorded(line.FromWarehouseCode, "—")}</td>");
            body.Append($"<td class=\"mod-dim ftd-batches\">{BatchesCell(line)}</td>");
            body.Append($"<td class=\"mod-r\">{Html(QuantityDisplay.Format(line.Quantity))}</td>");
            if (countOf(line) is decimal accepted)
            {
                var diff = accepted - line.Quantity;
                body.Append($"<td class=\"mod-r mod-linetotal\">{Html(QuantityDisplay.Format(accepted))}</td>");
                body.Append($"<td class=\"mod-r{(diff == 0 ? " mod-dim" : " ftd-diff")}\">{Html(DiffText(diff, draft))}</td>");
            }
            else
            {
                // A draft leaves the box for the receiver's pen; a cancelled transfer had nothing to count.
                body.Append(draft
                    ? "<td class=\"mod-r ftd-nowrap\"><span class=\"mod-none\">Not counted</span></td>"
                    : "<td class=\"mod-r\"><span class=\"mod-none\">—</span></td>");
                body.Append("<td class=\"mod-r\"><span class=\"mod-none\">—</span></td>");
            }
            body.Append("</tr>");
        }

        if (lines.Count == 0)
            body.Append($"<tr><td colspan=\"{(showSources ? 8 : 7)}\"><span class=\"mod-none\">No products</span></td></tr>");

        body.Append("</tbody></table>");
    }

    private static string BatchesCell(FactoryTransferLineDto line)
        => line.Batches.Count == 0
            ? "<span class=\"mod-none\">—</span>"
            : string.Join("<br>", line.Batches.Select(batch =>
                Html($"{batch.BatchNumber} · {QuantityDisplay.Format(batch.Quantity)}")));

    private static void AppendSignatures(StringBuilder body, FactoryTransferSummaryDto summary, bool delivery, bool unsent)
    {
        var receiver = string.IsNullOrWhiteSpace(summary.DestinationWarehouseCode) ? "destination" : summary.DestinationWarehouseCode;
        var sender = unsent ? null : summary.SentByName;

        body.Append($"<div class=\"ftd-signs{(delivery ? " ftd-signs-3" : "")}\">");
        AppendSignature(body, delivery ? "Loaded by" : "Sent by", sender);
        if (delivery)
            AppendSignature(body, "Driver", summary.DriverName);
        AppendSignature(body, $"Received into {receiver} by", null);
        body.Append("</div>");
    }

    private static void AppendSignature(StringBuilder body, string label, string? name)
    {
        body.Append("<div class=\"ftd-sign\">");
        body.Append($"<div class=\"ftd-sign-label\">{Html(label)}</div>");
        body.Append("<div class=\"ftd-sign-line\"></div>");
        body.Append($"<div class=\"ftd-sign-meta\"><span>{(string.IsNullOrWhiteSpace(name) ? "Name &amp; signature" : $"{Html(name)} · signature")}</span><span>Date</span></div>");
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

    private static string DisplayNumber(FactoryTransferSummaryDto summary)
        => string.IsNullOrWhiteSpace(summary.Number) ? $"#{summary.Id.ToString(CultureInfo.InvariantCulture)}" : summary.Number;

    /// <summary>"Yoghurt release", "Graniteside depot delivery".</summary>
    private static string Eyebrow(FactoryTransferSummaryDto summary)
    {
        var direction = FactoryTransferStatus.DirectionWord(summary.Direction);
        return string.IsNullOrWhiteSpace(summary.DepartmentName)
            ? $"Factory {direction}"
            : $"{summary.DepartmentName} {direction}";
    }

    private static string RouteText(FactoryTransferSummaryDto summary)
    {
        var from = string.IsNullOrWhiteSpace(summary.SourceLabel) ? summary.SourceWarehouseCode : summary.SourceLabel;
        return string.Join(" → ", new[] { from, summary.HoldingWarehouseCode, summary.DestinationWarehouseCode }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string DecidedWord(FactoryTransferSummaryDto summary) => summary.Status switch
    {
        FactoryTransferStatus.Rejected => "Rejected",
        FactoryTransferStatus.Recalled => "Recalled",
        FactoryTransferStatus.Cancelled => "Cancelled",
        FactoryTransferStatus.PartiallyAccepted => "Part-accepted",
        _ => "Accepted"
    };

    /// <summary>
    /// Accepted less sent. Counts never exceed what was sent, so the difference is only ever a
    /// shortfall: still "short" on a draft, and "returned" once settled, because the API sends
    /// whatever was not accepted back to the line's source.
    /// </summary>
    private static string DiffText(decimal diff, bool draft) => diff switch
    {
        0 => "Matches",
        < 0 => draft ? $"−{QuantityDisplay.Format(-diff)} short" : $"{QuantityDisplay.Format(-diff)} returned",
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

    /// <summary>Only what the note adds to the shared document sheet: column widths and the signatures.</summary>
    private const string NoteStyles = """

        .ftd-w-from { width: 72px; }
        .ftd-w-batch { width: 112px; }
        .ftd-w-qty { width: 72px; }
        .ftd-batches { font-size: 10.5px; }
        .ftd-diff { color: var(--mod-accent); font-weight: 500; white-space: nowrap; }
        .ftd-nowrap { white-space: nowrap; }

        /* A draft's stamp has to be read at a glance, as on the breakage slip. */
        @media print {
          .ftd-watermark-draft span {
            font-size: 132px;
            letter-spacing: 0.16em;
            color: rgba(93, 82, 148, 0.14);
          }
        }

        .ftd-signs {
          display: grid;
          grid-template-columns: 1fr 1fr;
          column-gap: 48px;
          margin-top: 48px;
          break-inside: avoid;
        }
        .ftd-signs-3 { grid-template-columns: 1fr 1fr 1fr; column-gap: 32px; }
        .ftd-sign-label {
          font-size: 9.5px;
          letter-spacing: 0.13em;
          text-transform: uppercase;
          color: var(--mod-mute);
        }
        .ftd-sign-line {
          height: 44px;
          border-bottom: 1px solid var(--mod-quiet);
        }
        .ftd-sign-meta {
          display: flex;
          justify-content: space-between;
          margin-top: 6px;
          font-size: 11px;
          color: var(--mod-faint);
        }
        """;
}
