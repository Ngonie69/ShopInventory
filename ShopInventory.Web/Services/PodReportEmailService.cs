using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public enum PodReportEmailPeriodKind
{
    Weekly,
    Monthly
}

public sealed record PodReportEmailSendResult(
    bool Success,
    string Message,
    int TotalInvoices = 0,
    int UploadedCount = 0,
    int PendingCount = 0);

public interface IPodReportEmailService
{
    Task<PodReportEmailSendResult> SendLatestAsync(
        PodReportEmailPeriodKind periodKind,
        string triggeredBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the POD report for the period implied by the schedule's frequency to the schedule's
    /// own recipient list. Used by both the manual "Send now" action and the background scheduler.
    /// </summary>
    Task<PodReportEmailSendResult> SendForScheduleAsync(
        PodReportEmailSchedule schedule,
        string triggeredBy,
        CancellationToken cancellationToken = default);
}

public sealed class PodReportEmailService(
    IHttpClientFactory httpClientFactory,
    IReportExportService reportExportService,
    IEmailService emailService,
    IAppSettingsService settingsService,
    IAuditService auditService,
    ILogger<PodReportEmailService> logger) : IPodReportEmailService
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const int ResponseBodyLogLimit = 4000;
    private static readonly EventId SendStartedEvent = new(7200, "PodReportEmailSendStarted");
    private static readonly EventId ReportApiStartedEvent = new(7201, "PodReportEmailReportApiStarted");
    private static readonly EventId ReportApiCompletedEvent = new(7202, "PodReportEmailReportApiCompleted");
    private static readonly EventId ReportGeneratedEvent = new(7203, "PodReportEmailReportGenerated");
    private static readonly EventId DeliveryStartedEvent = new(7204, "PodReportEmailDeliveryStarted");
    private static readonly EventId SendCompletedEvent = new(7205, "PodReportEmailSendCompleted");
    private static readonly EventId SendFailedEvent = new(7206, "PodReportEmailSendFailed");

    public async Task<PodReportEmailSendResult> SendLatestAsync(
        PodReportEmailPeriodKind periodKind,
        string triggeredBy,
        CancellationToken cancellationToken = default)
    {
        var frequency = periodKind == PodReportEmailPeriodKind.Monthly
            ? PodReportEmailFrequency.Monthly
            : PodReportEmailFrequency.Weekly;
        var (fromDate, toDate) = GetPeriod(frequency, null, PodScheduleTime.NowLocal());
        var frequencyLabel = GetFrequencyLabel(frequency, null);
        var fileSlug = GetFrequencySlug(frequency, null);
        var nowUtc = DateTime.UtcNow;
        var schedule = new PodReportEmailSchedule
        {
            Name = periodKind == PodReportEmailPeriodKind.Monthly
                ? "Manual full-month POD report"
                : "Manual weekly POD report",
            Enabled = true,
            Frequency = frequency.ToString(),
            ToRecipients = await settingsService.GetValueAsync(SettingKeys.PodReportEmailsTo) ?? string.Empty,
            CcRecipients = await settingsService.GetValueAsync(SettingKeys.PodReportEmailsCc) ?? string.Empty,
            AnchorDateUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            LastModifiedAtUtc = nowUtc,
            LastModifiedBy = triggeredBy
        };

        return await SendCoreAsync(
            schedule,
            frequencyLabel,
            fileSlug,
            fromDate,
            toDate,
            ParseRecipients(schedule.ToRecipients),
            ParseRecipients(schedule.CcRecipients),
            triggeredBy,
            cancellationToken);
    }

    public async Task<PodReportEmailSendResult> SendForScheduleAsync(
        PodReportEmailSchedule schedule,
        string triggeredBy,
        CancellationToken cancellationToken = default)
    {
        var frequency = ParseFrequency(schedule.Frequency);
        var (fromDate, toDate) = GetPeriod(frequency, schedule.IntervalDays, PodScheduleTime.NowLocal());
        var frequencyLabel = GetFrequencyLabel(frequency, schedule.IntervalDays);
        var fileSlug = GetFrequencySlug(frequency, schedule.IntervalDays);
        var to = ParseRecipients(schedule.ToRecipients);
        var cc = ParseRecipients(schedule.CcRecipients);

        return await SendCoreAsync(
            schedule,
            frequencyLabel,
            fileSlug,
            fromDate,
            toDate,
            to,
            cc,
            triggeredBy,
            cancellationToken);
    }

    private async Task<PodReportEmailSendResult> SendCoreAsync(
        PodReportEmailSchedule schedule,
        string frequencyLabel,
        string fileSlug,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyList<string> to,
        IReadOnlyList<string> cc,
        string triggeredBy,
        CancellationToken cancellationToken)
    {
        var fromText = FormatDateForLog(fromDate);
        var toText = FormatDateForLog(toDate);
        var elapsed = Stopwatch.StartNew();

        if (to.Count == 0)
        {
            logger.LogWarning(
                SendFailedEvent,
                "POD report email send skipped. Reason={FailureReason}, ScheduleId={ScheduleId}, ScheduleName={ScheduleName}, Frequency={Frequency}, FromDate={FromDate}, ToDate={ToDate}, TriggeredBy={TriggeredBy}",
                "MissingToRecipients",
                schedule.Id,
                schedule.Name,
                frequencyLabel,
                fromText,
                toText,
                triggeredBy);
            await LogPodReportEmailFailureAuditAsync(
                schedule,
                frequencyLabel,
                fromDate,
                toDate,
                triggeredBy,
                "MissingToRecipients",
                "Add at least one To recipient before sending POD report emails.");
            return new PodReportEmailSendResult(false, "Add at least one To recipient before sending POD report emails.");
        }

        logger.LogInformation(
            SendStartedEvent,
            "POD report email send started. ScheduleId={ScheduleId}, ScheduleName={ScheduleName}, Frequency={Frequency}, FromDate={FromDate}, ToDate={ToDate}, TriggeredBy={TriggeredBy}, RecipientCount={RecipientCount}, CcCount={CcCount}",
            schedule.Id,
            schedule.Name,
            frequencyLabel,
            fromText,
            toText,
            triggeredBy,
            to.Count,
            cc.Count);

        try
        {
            var (report, reportFailureDetail) = await GetPodReportAsync(fromDate, toDate, cancellationToken);
            if (report is null)
            {
                var failureMessage = string.IsNullOrWhiteSpace(reportFailureDetail)
                    ? "The POD report could not be generated from the API."
                    : $"The POD report could not be generated from the API. {reportFailureDetail}";
                logger.LogError(
                    SendFailedEvent,
                    "POD report email send failed before SMTP delivery. Reason={FailureReason}, FailureDetail={FailureDetail}, ScheduleId={ScheduleId}, ScheduleName={ScheduleName}, Frequency={Frequency}, FromDate={FromDate}, ToDate={ToDate}, TriggeredBy={TriggeredBy}, RecipientCount={RecipientCount}, CcCount={CcCount}, ElapsedMs={ElapsedMs}",
                    "ReportApiFailed",
                    reportFailureDetail,
                    schedule.Id,
                    schedule.Name,
                    frequencyLabel,
                    fromText,
                    toText,
                    triggeredBy,
                    to.Count,
                    cc.Count,
                    elapsed.ElapsedMilliseconds);
                await LogPodReportEmailFailureAuditAsync(
                    schedule,
                    frequencyLabel,
                    fromDate,
                    toDate,
                    triggeredBy,
                    "ReportApiFailed",
                    failureMessage);
                return new PodReportEmailSendResult(false, failureMessage);
            }

            // Keep the headline figures, invoicing-point breakdown, audit totals and workbook
            // on the same SAP-aligned reporting scope. This removes excluded creators/business
            // partners and invoices that have no official invoicing-point mapping.
            report = ReportExportService.ApplyPodReportingScope(report);

            var excel = reportExportService.ExportPodUploadStatusToExcel(report);
            var periodLabel = FormatPeriod(fromDate, toDate);
            var subject = $"{frequencyLabel} POD Report - {periodLabel}";
            var fileName = $"pod-report-{fileSlug}-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.xlsx";
            var htmlBody = BuildEmailBody(report, frequencyLabel, fromDate, toDate, triggeredBy);

            logger.LogInformation(
                ReportGeneratedEvent,
                "POD report data generated. ScheduleId={ScheduleId}, Frequency={Frequency}, FromDate={FromDate}, ToDate={ToDate}, TotalInvoices={TotalInvoices}, UploadedCount={UploadedCount}, PendingCount={PendingCount}, ItemCount={ItemCount}",
                schedule.Id,
                frequencyLabel,
                fromText,
                toText,
                report.TotalInvoices,
                report.UploadedCount,
                report.PendingCount,
                report.Items?.Count ?? 0);

            logger.LogInformation(
                DeliveryStartedEvent,
                "POD report email delivery starting. ScheduleId={ScheduleId}, ScheduleName={ScheduleName}, Frequency={Frequency}, Subject={Subject}, FileName={FileName}, AttachmentBytes={AttachmentBytes}, RecipientCount={RecipientCount}, CcCount={CcCount}, TriggeredBy={TriggeredBy}",
                schedule.Id,
                schedule.Name,
                frequencyLabel,
                subject,
                fileName,
                excel.LongLength,
                to.Count,
                cc.Count,
                triggeredBy);

            var emailResult = await emailService.SendEmailWithDiagnosticsAsync(
                to,
                cc,
                subject,
                htmlBody,
                attachments:
                [
                    new EmailAttachmentContent(fileName, ExcelContentType, excel)
                ],
                cancellationToken: cancellationToken);

            if (!emailResult.Success)
            {
                var emailFailure = FormatEmailFailure(emailResult);
                logger.LogError(
                    SendFailedEvent,
                    "POD report email delivery failed. Reason={FailureReason}, EmailFailureStage={EmailFailureStage}, EmailExceptionType={EmailExceptionType}, EmailFailureMessage={EmailFailureMessage}, ScheduleId={ScheduleId}, ScheduleName={ScheduleName}, Frequency={Frequency}, FromDate={FromDate}, ToDate={ToDate}, TriggeredBy={TriggeredBy}, Subject={Subject}, RecipientCount={RecipientCount}, CcCount={CcCount}, TotalInvoices={TotalInvoices}, UploadedCount={UploadedCount}, PendingCount={PendingCount}, AttachmentBytes={AttachmentBytes}, ElapsedMs={ElapsedMs}",
                    "EmailServiceReturnedFalse",
                    emailResult.FailureStage,
                    emailResult.ExceptionType,
                    emailResult.FailureMessage,
                    schedule.Id,
                    schedule.Name,
                    frequencyLabel,
                    fromText,
                    toText,
                    triggeredBy,
                    subject,
                    to.Count,
                    cc.Count,
                    report.TotalInvoices,
                    report.UploadedCount,
                    report.PendingCount,
                    excel.LongLength,
                    elapsed.ElapsedMilliseconds);
                await LogPodReportEmailFailureAuditAsync(
                    schedule,
                    frequencyLabel,
                    fromDate,
                    toDate,
                    triggeredBy,
                    emailResult.FailureStage ?? "EmailDeliveryFailed",
                    emailFailure);
                return new PodReportEmailSendResult(false, $"Failed to send the {frequencyLabel.ToLowerInvariant()} POD report email. {emailFailure}");
            }

            logger.LogInformation(
                SendCompletedEvent,
                "{Frequency} POD report emailed to {Recipients} for {FromDate} - {ToDate}. Schedule={ScheduleName} (#{ScheduleId}). Triggered by {TriggeredBy}.",
                frequencyLabel,
                string.Join(", ", to),
                fromText,
                toText,
                schedule.Name,
                schedule.Id,
                triggeredBy);

            logger.LogInformation(
                SendCompletedEvent,
                "POD report email send completed. ScheduleId={ScheduleId}, Frequency={Frequency}, FromDate={FromDate}, ToDate={ToDate}, TriggeredBy={TriggeredBy}, RecipientCount={RecipientCount}, CcCount={CcCount}, TotalInvoices={TotalInvoices}, UploadedCount={UploadedCount}, PendingCount={PendingCount}, AttachmentBytes={AttachmentBytes}, ElapsedMs={ElapsedMs}",
                schedule.Id,
                frequencyLabel,
                fromText,
                toText,
                triggeredBy,
                to.Count,
                cc.Count,
                report.TotalInvoices,
                report.UploadedCount,
                report.PendingCount,
                excel.LongLength,
                elapsed.ElapsedMilliseconds);

            return new PodReportEmailSendResult(
                true,
                $"{frequencyLabel} POD report sent to {to.Count} recipient{(to.Count == 1 ? string.Empty : "s")}.",
                report.TotalInvoices,
                report.UploadedCount,
                report.PendingCount);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            const string failureMessage = "The POD report API did not complete within the configured timeout.";
            logger.LogError(
                SendFailedEvent,
                ex,
                "POD report email send timed out before SMTP delivery. Reason={FailureReason}, ScheduleId={ScheduleId}, ScheduleName={ScheduleName}, Frequency={Frequency}, FromDate={FromDate}, ToDate={ToDate}, TriggeredBy={TriggeredBy}, RecipientCount={RecipientCount}, CcCount={CcCount}, ElapsedMs={ElapsedMs}",
                "ReportApiTimeout",
                schedule.Id,
                schedule.Name,
                frequencyLabel,
                fromText,
                toText,
                triggeredBy,
                to.Count,
                cc.Count,
                elapsed.ElapsedMilliseconds);
            await LogPodReportEmailFailureAuditAsync(
                schedule,
                frequencyLabel,
                fromDate,
                toDate,
                triggeredBy,
                "ReportApiTimeout",
                failureMessage);
            return new PodReportEmailSendResult(false, failureMessage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                SendFailedEvent,
                ex,
                "Failed to send {Frequency} POD report email for {FromDate} - {ToDate}. ScheduleId={ScheduleId}, ScheduleName={ScheduleName}, TriggeredBy={TriggeredBy}, RecipientCount={RecipientCount}, CcCount={CcCount}, ElapsedMs={ElapsedMs}, ExceptionType={ExceptionType}, FailureMessage={FailureMessage}",
                frequencyLabel,
                fromText,
                toText,
                schedule.Id,
                schedule.Name,
                triggeredBy,
                to.Count,
                cc.Count,
                elapsed.ElapsedMilliseconds,
                ex.GetType().Name,
                ex.Message);
            await LogPodReportEmailFailureAuditAsync(
                schedule,
                frequencyLabel,
                fromDate,
                toDate,
                triggeredBy,
                ex.GetType().Name,
                ex.Message);
            return new PodReportEmailSendResult(false, $"Failed to send POD report email: {ex.Message}");
        }
    }

    /// <summary>
    /// Computes the report data window (date range) for a frequency, relative to <paramref name="nowLocal"/>.
    /// Standard cadences cover the most recently completed period (ending yesterday). The
    /// month-to-date daily cadence intentionally includes the send date so a day-1 schedule
    /// starts on the first of the current month and accumulates each day.
    /// </summary>
    public static (DateTime fromDate, DateTime toDate) GetPeriod(
        PodReportEmailFrequency frequency,
        int? intervalDays,
        DateTime nowLocal)
    {
        var toDate = nowLocal.Date.AddDays(-1);

        return frequency switch
        {
            PodReportEmailFrequency.Daily => (toDate, toDate),
            PodReportEmailFrequency.MonthToDateDaily => GetCurrentMonthToDatePeriod(nowLocal),
            PodReportEmailFrequency.Weekly => (toDate.AddDays(-6), toDate),
            PodReportEmailFrequency.Monthly => GetPreviousCalendarMonthPeriod(nowLocal),
            PodReportEmailFrequency.Quarterly => GetPreviousCalendarPeriod(nowLocal, 3),
            PodReportEmailFrequency.HalfYearly => GetPreviousCalendarPeriod(nowLocal, 6),
            PodReportEmailFrequency.EveryNDays => (toDate.AddDays(-(NormalizeIntervalDays(intervalDays) - 1)), toDate),
            _ => (toDate.AddDays(-6), toDate)
        };
    }

    public static PodReportEmailFrequency ParseFrequency(string? value) =>
        Enum.TryParse<PodReportEmailFrequency>(value, ignoreCase: true, out var parsed)
            ? parsed
            : PodReportEmailFrequency.Weekly;

    public static string GetFrequencyLabel(PodReportEmailFrequency frequency, int? intervalDays) =>
        frequency switch
        {
            PodReportEmailFrequency.Daily => "Daily",
            PodReportEmailFrequency.MonthToDateDaily => "Month to Date",
            PodReportEmailFrequency.Weekly => "Weekly",
            PodReportEmailFrequency.Monthly => "Full Month",
            PodReportEmailFrequency.Quarterly => "Quarterly",
            PodReportEmailFrequency.HalfYearly => "Half-yearly",
            PodReportEmailFrequency.EveryNDays => $"Every {NormalizeIntervalDays(intervalDays)} days",
            _ => "Weekly"
        };

    public static int NormalizeIntervalDays(int? intervalDays) => Math.Max(1, intervalDays ?? 1);

    private static string GetFrequencySlug(PodReportEmailFrequency frequency, int? intervalDays) =>
        frequency switch
        {
            PodReportEmailFrequency.Daily => "daily",
            PodReportEmailFrequency.MonthToDateDaily => "month-to-date",
            PodReportEmailFrequency.Weekly => "weekly",
            PodReportEmailFrequency.Monthly => "monthly",
            PodReportEmailFrequency.Quarterly => "quarterly",
            PodReportEmailFrequency.HalfYearly => "half-yearly",
            PodReportEmailFrequency.EveryNDays => $"every-{NormalizeIntervalDays(intervalDays)}-days",
            _ => "weekly"
        };

    private static (DateTime fromDate, DateTime toDate) GetPreviousCalendarMonthPeriod(DateTime nowLocal)
    {
        var monthStart = new DateTime(nowLocal.Year, nowLocal.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var fromDate = monthStart.AddMonths(-1);
        var toDate = monthStart.AddDays(-1);
        return (fromDate, toDate);
    }

    private static (DateTime fromDate, DateTime toDate) GetPreviousCalendarPeriod(
        DateTime nowLocal,
        int monthsPerPeriod)
    {
        var currentPeriodStartMonth = ((nowLocal.Month - 1) / monthsPerPeriod) * monthsPerPeriod + 1;
        var currentPeriodStart = new DateTime(
            nowLocal.Year,
            currentPeriodStartMonth,
            1,
            0,
            0,
            0,
            DateTimeKind.Unspecified);

        return (currentPeriodStart.AddMonths(-monthsPerPeriod), currentPeriodStart.AddDays(-1));
    }

    private static (DateTime fromDate, DateTime toDate) GetCurrentMonthToDatePeriod(DateTime nowLocal)
    {
        var fromDate = new DateTime(nowLocal.Year, nowLocal.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var toDate = nowLocal.Date;
        return (fromDate, toDate);
    }

    private async Task<(PodUploadStatusReport? Report, string? FailureDetail)> GetPodReportAsync(
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        // A month-to-date/full-month report can legitimately take longer than the standard
        // five-minute API timeout while SAP and local POD status are combined. Use the existing
        // long-running client so the scheduler does not cancel a valid report before SMTP starts.
        var client = httpClientFactory.CreateClient("ShopInventoryApiLongRunning");
        var from = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var requestPath = $"api/invoice/pod-upload-status?fromDate={from}&toDate={to}&includeCreditNoteActivity=true";

        logger.LogInformation(
            ReportApiStartedEvent,
            "POD report API request starting. RequestPath={RequestPath}, FromDate={FromDate}, ToDate={ToDate}",
            requestPath,
            from,
            to);

        using var response = await client.GetAsync(
            requestPath,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                SendFailedEvent,
                "POD report API call failed. RequestPath={RequestPath}, FromDate={FromDate}, ToDate={ToDate}, StatusCode={StatusCode}, ReasonPhrase={ReasonPhrase}, ResponseBody={ResponseBody}",
                requestPath,
                from,
                to,
                (int)response.StatusCode,
                response.ReasonPhrase,
                TruncateForLog(body, ResponseBodyLogLimit));
            return (null, BuildApiFailureDetail((int)response.StatusCode, response.ReasonPhrase, body));
        }

        var report = await response.Content.ReadFromJsonAsync<PodUploadStatusReport>(cancellationToken);
        if (report is null)
        {
            logger.LogWarning(
                SendFailedEvent,
                "POD report API response deserialized to null. RequestPath={RequestPath}, FromDate={FromDate}, ToDate={ToDate}, StatusCode={StatusCode}",
                requestPath,
                from,
                to,
                (int)response.StatusCode);
            return (null, $"The report API returned HTTP {(int)response.StatusCode} with an empty body.");
        }

        logger.LogInformation(
            ReportApiCompletedEvent,
            "POD report API request completed. RequestPath={RequestPath}, FromDate={FromDate}, ToDate={ToDate}, TotalInvoices={TotalInvoices}, UploadedCount={UploadedCount}, PendingCount={PendingCount}, ItemCount={ItemCount}",
            requestPath,
            from,
            to,
            report.TotalInvoices,
            report.UploadedCount,
            report.PendingCount,
            report.Items?.Count ?? 0);

        return (report, null);
    }

    /// <summary>
    /// Turns a failed report-API response into a short, human-readable reason suitable for the
    /// failure audit entry. Prefers the ProblemDetails <c>detail</c>/<c>title</c> when the API
    /// returns one (e.g. the SAP timeout/connection messages), falling back to a truncated body.
    /// </summary>
    private static string BuildApiFailureDetail(int statusCode, string? reasonPhrase, string? body)
    {
        var reason = string.IsNullOrWhiteSpace(reasonPhrase) ? null : reasonPhrase.Trim();
        var apiMessage = TryExtractProblemDetail(body);

        if (string.IsNullOrWhiteSpace(apiMessage))
        {
            apiMessage = string.IsNullOrWhiteSpace(body)
                ? null
                : TruncateForLog(body.Trim(), ResponseBodyLogLimit);
        }

        var prefix = reason is null
            ? $"The report API returned HTTP {statusCode}."
            : $"The report API returned HTTP {statusCode} ({reason}).";

        return string.IsNullOrWhiteSpace(apiMessage) ? prefix : $"{prefix} {apiMessage}";
    }

    private static string? TryExtractProblemDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in new[] { "detail", "Detail", "message", "Message", "title", "Title" })
            {
                if (document.RootElement.TryGetProperty(property, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return TruncateForLog(text.Trim(), ResponseBodyLogLimit);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (or malformed) — the caller falls back to the raw truncated body.
        }

        return null;
    }

    private static string BuildEmailBody(
        PodUploadStatusReport report,
        string frequencyLabel,
        DateTime fromDate,
        DateTime toDate,
        string triggeredBy)
    {
        var period = FormatPeriod(fromDate, toDate);
        var completion = report.TotalInvoices == 0
            ? 0m
            : report.UploadedCount * 100m / report.TotalInvoices;
        var invoicingPoints = report.Items
            .GroupBy(
                item => item.CreatedLocation!.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.Key,
                TotalInvoices = group.Count(),
                UploadedCount = group.Count(item => item.HasPod),
                PendingCount = group.Count(item => !item.HasPod)
            })
            .OrderByDescending(point => point.PendingCount)
            .ThenByDescending(point => point.TotalInvoices)
            .ThenBy(point => point.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var invoicingPointRows = new StringBuilder();
        foreach (var point in invoicingPoints)
        {
            var pointCompletion = point.TotalInvoices == 0
                ? 0m
                : point.UploadedCount * 100m / point.TotalInvoices;
            var completionColor = pointCompletion >= 85m
                ? "#2e7d32"
                : pointCompletion >= 60m
                    ? "#e65100"
                    : "#c62828";

            invoicingPointRows.Append($@"
                <tr>
                    <td style='padding: 9px; border: 1px solid #ddd;'>{WebUtility.HtmlEncode(point.Name)}</td>
                    <td style='padding: 9px; border: 1px solid #ddd; text-align: right;'>{point.TotalInvoices:N0}</td>
                    <td style='padding: 9px; border: 1px solid #ddd; text-align: right; color: #2e7d32;'>{point.UploadedCount:N0}</td>
                    <td style='padding: 9px; border: 1px solid #ddd; text-align: right; color: #c62828;'>{point.PendingCount:N0}</td>
                    <td style='padding: 9px; border: 1px solid #ddd; text-align: right; color: {completionColor}; font-weight: 600;'>{pointCompletion:N1}%</td>
                </tr>");
        }

        var breakdownTotal = invoicingPoints.Sum(point => point.TotalInvoices);
        var breakdownUploaded = invoicingPoints.Sum(point => point.UploadedCount);
        var breakdownPending = invoicingPoints.Sum(point => point.PendingCount);
        var breakdownCompletion = breakdownTotal == 0
            ? 0m
            : breakdownUploaded * 100m / breakdownTotal;
        var invoicingPointBreakdown = invoicingPoints.Count == 0
            ? "<p style='color: #666;'>No invoicing point data is available for this period.</p>"
            : $@"
                <table style='border-collapse: collapse; width: 100%; max-width: 760px;'>
                    <thead>
                        <tr style='background: #263b56; color: #fff;'>
                            <th style='padding: 9px; border: 1px solid #ddd; text-align: left;'>Invoicing Point</th>
                            <th style='padding: 9px; border: 1px solid #ddd; text-align: right;'>Total</th>
                            <th style='padding: 9px; border: 1px solid #ddd; text-align: right;'>POD Uploaded</th>
                            <th style='padding: 9px; border: 1px solid #ddd; text-align: right;'>Pending POD</th>
                            <th style='padding: 9px; border: 1px solid #ddd; text-align: right;'>Completion</th>
                        </tr>
                    </thead>
                    <tbody>
                        {invoicingPointRows}
                        <tr style='background: #e8eef5; font-weight: 700;'>
                            <td style='padding: 9px; border: 1px solid #ddd;'>All invoicing points</td>
                            <td style='padding: 9px; border: 1px solid #ddd; text-align: right;'>{breakdownTotal:N0}</td>
                            <td style='padding: 9px; border: 1px solid #ddd; text-align: right;'>{breakdownUploaded:N0}</td>
                            <td style='padding: 9px; border: 1px solid #ddd; text-align: right;'>{breakdownPending:N0}</td>
                            <td style='padding: 9px; border: 1px solid #ddd; text-align: right;'>{breakdownCompletion:N1}%</td>
                        </tr>
                    </tbody>
                </table>
                <p style='color: #666; font-size: 12px; margin-top: 8px;'>Rows are ordered by highest pending POD count.</p>";

        return $@"
            <h2>{WebUtility.HtmlEncode(frequencyLabel)} POD Report</h2>
            <p>The POD upload report for <strong>{WebUtility.HtmlEncode(period)}</strong> is attached as an Excel workbook.</p>
            <table style='border-collapse: collapse; width: 100%; max-width: 520px;'>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Total Invoices</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right;'>{report.TotalInvoices:N0}</td>
                </tr>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>POD Uploaded</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right;'>{report.UploadedCount:N0}</td>
                </tr>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Pending POD</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right;'>{report.PendingCount:N0}</td>
                </tr>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Completion</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right;'>{completion:N1}%</td>
                </tr>
            </table>
            <h3 style='margin-top: 24px; margin-bottom: 10px;'>Breakdown by Invoicing Point</h3>
            {invoicingPointBreakdown}
            <p style='color: #666; font-size: 13px; margin-top: 16px;'>Triggered by {WebUtility.HtmlEncode(triggeredBy)}.</p>";
    }

    private static string FormatPeriod(DateTime fromDate, DateTime toDate) =>
        $"{fromDate:dd MMM yyyy} - {toDate:dd MMM yyyy}";

    private static string FormatDateForLog(DateTime date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatEmailFailure(EmailSendResult emailResult)
    {
        var stage = string.IsNullOrWhiteSpace(emailResult.FailureStage)
            ? "EmailDeliveryFailed"
            : emailResult.FailureStage;
        var message = string.IsNullOrWhiteSpace(emailResult.FailureMessage)
            ? "The email service did not provide a failure message."
            : emailResult.FailureMessage;

        return string.IsNullOrWhiteSpace(emailResult.ExceptionType)
            ? $"{stage}: {message}"
            : $"{stage} ({emailResult.ExceptionType}): {message}";
    }

    private async Task LogPodReportEmailFailureAuditAsync(
        PodReportEmailSchedule schedule,
        string frequencyLabel,
        DateTime fromDate,
        DateTime toDate,
        string triggeredBy,
        string failureStage,
        string failureMessage)
    {
        try
        {
            var username = string.IsNullOrWhiteSpace(triggeredBy)
                ? "System"
                : triggeredBy;
            var role = string.Equals(username, "System schedule", StringComparison.OrdinalIgnoreCase)
                ? "System"
                : "Admin";
            var details = string.Join(
                " | ",
                [
                    $"Schedule={schedule.Name} (#{schedule.Id})",
                    $"Frequency={frequencyLabel}",
                    $"From={FormatDateForLog(fromDate)}",
                    $"To={FormatDateForLog(toDate)}",
                    $"TriggeredBy={username}",
                    $"FailureStage={failureStage}"
                ]);

            await auditService.LogAsync(
                AuditActions.SendPodReportEmail,
                username,
                role,
                "POD Report Email",
                $"{schedule.Name} (#{schedule.Id})",
                details,
                "/settings",
                isSuccess: false,
                errorMessage: TruncateForLog(failureMessage, 1000));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to write POD report email failure audit entry. ScheduleId={ScheduleId}, FailureStage={FailureStage}",
                schedule.Id,
                failureStage);
        }
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength] + "...";
    }

    private static List<string> ParseRecipients(string? value)
        => EmailRecipientParser.Parse(value);
}
