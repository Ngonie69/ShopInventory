using System.Globalization;
using System.Net;
using System.Text;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Builds and sends one van sales report email.
/// </summary>
/// <remarks>
/// <b>The caveats travel with the report.</b> Every page in this suite works hardest at saying what
/// its figures do not cover — which share of a period reached SAP, which currency has no margin,
/// which rep has no band. A pushed report reaches somebody who did not open the page and cannot see
/// any of that, so the quality caveats go into the body above the figures rather than being left
/// behind on a screen the recipient never visited. A report that arrives without them is more
/// dangerous than one that does not arrive.
///
/// <b>The window is derived from the cadence, never stored.</b> A daily send covers yesterday, a
/// weekly one the last seven days, and so on, always ending on the last complete trading day. Making
/// it configurable would let somebody set a weekly email reporting on a single day and spend a month
/// wondering why the totals looked wrong. The body states the window it used.
/// </remarks>
public interface IVanReportEmailService
{
    Task<VanReportEmailOutcome> SendAsync(
        VanReportEmailSchedule schedule,
        string triggeredBy,
        CancellationToken cancellationToken = default);
}

public sealed record VanReportEmailOutcome(bool Success, string? Error, int RecipientCount);

public sealed class VanReportEmailService(
    IVanSalesReportService reports,
    IReportExportService exports,
    IEmailService emailService,
    ILogger<VanReportEmailService> logger) : IVanReportEmailService
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>One report, rendered and ready to send.</summary>
    private sealed record Rendered(
        string Title,
        byte[] Workbook,
        string FileNameStem,
        IReadOnlyList<string> Caveats,
        IReadOnlyList<(string Label, string Value)> Headlines);

    public async Task<VanReportEmailOutcome> SendAsync(
        VanReportEmailSchedule schedule,
        string triggeredBy,
        CancellationToken cancellationToken = default)
    {
        var to = EmailRecipientParser.Parse(schedule.ToRecipients);

        if (to.Count == 0)
        {
            // Not an error worth retrying — a schedule with nobody to send to is a configuration
            // that has not been finished, and saying so is more use than a failed send.
            return new VanReportEmailOutcome(false, "The schedule has no recipients.", 0);
        }

        var cc = EmailRecipientParser.Parse(schedule.CcRecipients);
        var kind = ParseKind(schedule.ReportKind);
        var frequency = ParseFrequency(schedule.Frequency);
        var (from, until) = WindowFor(frequency, ReportScheduleCadence.NormalizeIntervalDays(schedule.IntervalDays));

        var rendered = await RenderAsync(kind, from, until);

        if (rendered is null)
        {
            // The reporting service swallows its own failures and returns null, so this is the only
            // signal that the API did not answer. Reported rather than sent as an empty report.
            return new VanReportEmailOutcome(
                false,
                $"The {kind} report could not be loaded. The reporting service did not answer.",
                to.Count);
        }

        var subject = $"{rendered.Title} — {from:d MMM} to {until:d MMM yyyy}";
        var fileName = $"{rendered.FileNameStem}_{until:yyyyMMdd}.xlsx";

        logger.LogInformation(
            "Van report email delivery starting. ScheduleId={ScheduleId}, Report={Report}, "
            + "Window={From:yyyy-MM-dd}..{Until:yyyy-MM-dd}, Recipients={Recipients}, TriggeredBy={TriggeredBy}",
            schedule.Id, kind, from, until, to.Count, triggeredBy);

        var result = await emailService.SendEmailWithDiagnosticsAsync(
            to,
            cc,
            subject,
            BuildBody(rendered, schedule, from, until),
            attachments: [new EmailAttachmentContent(fileName, ExcelContentType, rendered.Workbook)],
            cancellationToken: cancellationToken);

        if (!result.Success)
        {
            logger.LogError(
                "Van report email failed. ScheduleId={ScheduleId}, Report={Report}, Error={Error}",
                schedule.Id, kind, FormatFailure(result));

            return new VanReportEmailOutcome(false, FormatFailure(result), to.Count);
        }

        return new VanReportEmailOutcome(true, null, to.Count);
    }

    /// <summary>
    /// The failure as a reader of the schedule list will see it — stage as well as message, because
    /// "authentication failed" and "the recipient was rejected" call for different people.
    /// </summary>
    private static string FormatFailure(EmailSendResult result)
    {
        var parts = new[] { result.FailureStage, result.FailureMessage, result.ExceptionType }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        return parts.Count == 0 ? "The email was not accepted." : string.Join(" — ", parts);
    }

    // ── Which report ────────────────────────────────────────────────────────────

    private async Task<Rendered?> RenderAsync(VanReportKind kind, DateTime from, DateTime until)
    {
        switch (kind)
        {
            case VanReportKind.Scorecard:
            {
                var report = await reports.GetScorecardReportAsync(from, until);
                if (report is null) return null;

                return new Rendered(
                    "Van sales scorecard",
                    exports.ExportVanSalesScorecardToExcel(report),
                    "VanSalesScorecard",
                    report.Quality.Caveats.ToList(),
                    [
                        ("Strike rate", Rate(report.Summary.StrikeRate)),
                        ("Call compliance", Rate(report.Summary.CallComplianceRate)),
                        ("Outlets bought", report.Summary.OutletsBought.ToString("N0")),
                        ("Rows red", report.Summary.RedCount.ToString("N0"))
                    ]);
            }

            case VanReportKind.Exceptions:
            {
                var report = await reports.GetExceptionsReportAsync(from, until);
                if (report is null) return null;

                return new Rendered(
                    "Van sales exceptions",
                    exports.ExportVanSalesExceptionsToExcel(report),
                    "VanSalesExceptions",
                    report.Quality.Caveats.ToList(),
                    [
                        ("Unseen documents", report.Summary.UnseenDocumentCount.ToString("N0")),
                        ("Of those, expired", report.Summary.ExpiredDocumentCount.ToString("N0")),
                        ("Held for posting", report.Summary.HeldSaleCount.ToString("N0")),
                        ("Sales with no tender", report.Summary.SalesWithoutTender.ToString("N0"))
                    ]);
            }

            case VanReportKind.Margin:
            {
                var report = await reports.GetMarginReportAsync(from, until);
                if (report is null) return null;

                return new Rendered(
                    "Van margin",
                    exports.ExportVanMarginToExcel(report),
                    "VanMargin",
                    report.Quality.Caveats.ToList(),
                    [
                        ("Revenue", Money(report.Summary.RevenueByCurrency)),
                        ("SAP can cost", Rate(report.Summary.CostableLineShare)),
                        ("Margin", Margin(report.Summary.MarginByCurrency)),
                        ("Items sold", report.Summary.ItemCount.ToString("N0"))
                    ]);
            }

            case VanReportKind.Coverage:
            {
                var report = await reports.GetCoverageReportAsync(from, until);
                if (report is null) return null;

                return new Rendered(
                    "Van sales coverage",
                    exports.ExportVanSalesCoverageToExcel(report),
                    "VanSalesCoverage",
                    report.Quality.Caveats.ToList(),
                    [
                        ("Outlets bought", report.Summary.OutletsBought.ToString("N0")),
                        ("Outlets not reached", report.Summary.OutletsUncovered.ToString("N0")),
                        ("New outlets", report.Summary.NewOutlets.ToString("N0")),
                        ("Lapsed", report.Summary.LapsedOutlets.ToString("N0"))
                    ]);
            }

            case VanReportKind.Performance:
            {
                var report = await reports.GetPerformanceReportAsync(from, until);
                if (report is null) return null;

                return new Rendered(
                    "Van sales performance",
                    exports.ExportVanSalesPerformanceToExcel(report),
                    "VanSalesPerformance",
                    report.Coverage.Caveats.ToList(),
                    [
                        ("Takings", Money(report.Summary.TotalsByCurrency)),
                        ("Productive calls", report.Summary.ProductiveCalls.ToString("N0")),
                        ("Outlets", report.Summary.CustomerCount.ToString("N0")),
                        ("Items", report.Summary.ItemCount.ToString("N0"))
                    ]);
            }

            case VanReportKind.Stock:
            {
                var report = await reports.GetStockReportAsync(from, until);
                if (report is null) return null;

                return new Rendered(
                    "Van stock",
                    exports.ExportVanStockToExcel(report),
                    "VanStock",
                    report.Quality.Caveats.ToList(),
                    [
                        ("Vans", report.Summary.VanCount.ToString("N0")),
                        ("Snapshot days", report.Summary.SnapshotDayCount.ToString("N0")),
                        ("Missing days", report.Summary.MissingSnapshotDays.ToString("N0")),
                        ("Dead lines", report.Summary.DeadItemCount.ToString("N0"))
                    ]);
            }

            default:
            {
                var report = await reports.GetReplenishmentReportAsync(from, until);
                if (report is null) return null;

                return new Rendered(
                    "Van replenishment",
                    exports.ExportVanReplenishmentToExcel(report),
                    "VanReplenishment",
                    report.Quality.Caveats.ToList(),
                    [
                        ("Requests", report.Summary.RequestCount.ToString("N0")),
                        ("Awaiting approval", report.Summary.AwaitingApprovalCount.ToString("N0")),
                        ("Failed to post", report.Summary.PostFailedCount.ToString("N0")),
                        ("Rejected", report.Summary.RejectedCount.ToString("N0"))
                    ]);
            }
        }
    }

    // ── The window ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The period a scheduled send covers, ending on the last complete trading day.
    /// </summary>
    /// <remarks>
    /// Ends yesterday, never today: a report sent at 06:00 that included today would cover the three
    /// hours of trading before it went out and read as a catastrophic collapse. Rolling rather than
    /// calendar-aligned, one rule for every frequency, and the email says which dates it used.
    /// </remarks>
    internal static (DateTime From, DateTime Until) WindowFor(
        ReportScheduleFrequency frequency,
        int intervalDays)
    {
        // A trading day belongs to the van, so today is CAT rather than UTC.
        var until = PodScheduleTime.NowLocal().Date.AddDays(-1);

        var length = frequency switch
        {
            ReportScheduleFrequency.Daily => 1,
            ReportScheduleFrequency.EveryNDays => Math.Max(1, intervalDays),
            ReportScheduleFrequency.Weekly => 7,
            ReportScheduleFrequency.Monthly => 30,
            ReportScheduleFrequency.Quarterly => 90,
            ReportScheduleFrequency.HalfYearly => 180,
            _ => 7
        };

        if (frequency == ReportScheduleFrequency.MonthToDateDaily)
        {
            // The one window that is not rolling: month to date is what the name promises.
            return (new DateTime(until.Year, until.Month, 1), until);
        }

        return (until.AddDays(-(length - 1)), until);
    }

    // ── The body ────────────────────────────────────────────────────────────────

    private static string BuildBody(
        Rendered rendered,
        VanReportEmailSchedule schedule,
        DateTime from,
        DateTime until)
    {
        var body = new StringBuilder();

        body.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1a1a2e\">");
        body.Append($"<h2 style=\"margin:0 0 4px\">{Encode(rendered.Title)}</h2>");
        body.Append($"<p style=\"margin:0 0 16px;color:#5a6a7a\">{from:d MMM yyyy} to {until:d MMM yyyy} "
                    + $"&middot; {Encode(schedule.Name)}</p>");

        body.Append("<table cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse;margin-bottom:16px\">");
        foreach (var (label, value) in rendered.Headlines)
        {
            body.Append("<tr>")
                .Append($"<td style=\"border:1px solid #dde3ea;color:#5a6a7a\">{Encode(label)}</td>")
                .Append($"<td style=\"border:1px solid #dde3ea;font-weight:600\">{Encode(value)}</td>")
                .Append("</tr>");
        }
        body.Append("</table>");

        // Above the attachment line, not below it. Somebody who reads only the first screen of an
        // email has to meet the limits before they meet the numbers.
        if (rendered.Caveats.Count > 0)
        {
            body.Append("<div style=\"border-left:3px solid #e65100;padding:8px 12px;background:#fff8f0;"
                        + "margin-bottom:16px\">");
            body.Append("<div style=\"font-weight:600;color:#e65100;margin-bottom:4px\">"
                        + "What this report does not tell you</div><ul style=\"margin:0;padding-left:18px\">");

            foreach (var caveat in rendered.Caveats)
            {
                body.Append($"<li style=\"margin-bottom:4px\">{Encode(caveat)}</li>");
            }

            body.Append("</ul></div>");
        }

        body.Append("<p style=\"color:#5a6a7a\">The full report is attached as a spreadsheet. "
                    + "It repeats everything above and carries the detail behind it.</p>");
        body.Append("</div>");

        return body.ToString();
    }

    // ── Parsing and formatting ──────────────────────────────────────────────────

    /// <summary>Falls back to the scorecard rather than throwing on a value nobody recognises.</summary>
    internal static VanReportKind ParseKind(string? value) =>
        Enum.TryParse<VanReportKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : VanReportKind.Scorecard;

    internal static ReportScheduleFrequency ParseFrequency(string? value) =>
        Enum.TryParse<ReportScheduleFrequency>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ReportScheduleFrequency.Weekly;

    private static string Rate(double? value) =>
        value?.ToString("P0", CultureInfo.InvariantCulture) ?? "—";

    private static string Money(List<VanSalesMoney> totals) =>
        totals.Count == 0 ? "—" : string.Join("  ·  ", totals.Select(t => $"{t.Currency} {t.Gross:N2}"));

    private static string Money(List<VanSalesLineMoney> totals) =>
        totals.Count == 0 ? "—" : string.Join("  ·  ", totals.Select(t => $"{t.Currency} {t.Gross:N2}"));

    private static string Margin(List<VanMarginMoney> margins) =>
        margins.Count == 0 ? "—" : string.Join("  ·  ", margins.Select(m => $"{m.Currency} {m.Margin:N2}"));

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
