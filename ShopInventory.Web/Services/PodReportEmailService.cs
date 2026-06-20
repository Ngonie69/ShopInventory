using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public enum PodReportEmailPeriodKind
{
    Weekly,
    Monthly
}

public sealed record PodReportEmailScheduleOptions(
    bool Enabled,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    DayOfWeek WeeklyDayOfWeek,
    int WeeklySendHourUtc,
    int MonthlyDayOfMonth,
    int MonthlySendHourUtc,
    DateTime? LastWeeklySentUtc,
    DateTime? LastMonthlySentUtc);

public sealed record PodReportEmailSendResult(
    bool Success,
    string Message,
    int TotalInvoices = 0,
    int UploadedCount = 0,
    int PendingCount = 0);

public interface IPodReportEmailService
{
    Task<PodReportEmailScheduleOptions> GetOptionsAsync();
    Task<PodReportEmailSendResult> SendLatestAsync(
        PodReportEmailPeriodKind periodKind,
        string triggeredBy,
        CancellationToken cancellationToken = default);
    Task<PodReportEmailSendResult> SendForPeriodAsync(
        PodReportEmailPeriodKind periodKind,
        DateTime fromDate,
        DateTime toDate,
        string triggeredBy,
        CancellationToken cancellationToken = default);
    Task MarkScheduledSentAsync(
        PodReportEmailPeriodKind periodKind,
        DateTime scheduledUtc,
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

    public async Task<PodReportEmailScheduleOptions> GetOptionsAsync()
    {
        var enabled = ParseBool(await settingsService.GetValueAsync(SettingKeys.PodReportEmailsEnabled));
        var to = ParseRecipients(await settingsService.GetValueAsync(SettingKeys.PodReportEmailsTo));
        var cc = ParseRecipients(await settingsService.GetValueAsync(SettingKeys.PodReportEmailsCc));
        var weeklyDay = ParseDayOfWeek(
            await settingsService.GetValueAsync(SettingKeys.PodReportEmailsWeeklyDayOfWeek),
            DayOfWeek.Monday);
        var weeklyHour = ParseHour(await settingsService.GetValueAsync(SettingKeys.PodReportEmailsWeeklySendHourUtc), 6);
        var monthlyDay = ParseDayOfMonth(await settingsService.GetValueAsync(SettingKeys.PodReportEmailsMonthlyDayOfMonth), 1);
        var monthlyHour = ParseHour(await settingsService.GetValueAsync(SettingKeys.PodReportEmailsMonthlySendHourUtc), 6);
        var lastWeeklySent = ParseUtc(await settingsService.GetValueAsync(SettingKeys.PodReportEmailsLastWeeklySentUtc));
        var lastMonthlySent = ParseUtc(await settingsService.GetValueAsync(SettingKeys.PodReportEmailsLastMonthlySentUtc));

        return new PodReportEmailScheduleOptions(
            enabled,
            to,
            cc,
            weeklyDay,
            weeklyHour,
            monthlyDay,
            monthlyHour,
            lastWeeklySent,
            lastMonthlySent);
    }

    public async Task<PodReportEmailSendResult> SendLatestAsync(
        PodReportEmailPeriodKind periodKind,
        string triggeredBy,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var (fromDate, toDate) = periodKind == PodReportEmailPeriodKind.Weekly
            ? GetPreviousSevenDayPeriod(nowUtc)
            : GetPreviousCalendarMonthPeriod(nowUtc);

        return await SendForPeriodAsync(periodKind, fromDate, toDate, triggeredBy, cancellationToken);
    }

    public async Task<PodReportEmailSendResult> SendForPeriodAsync(
        PodReportEmailPeriodKind periodKind,
        DateTime fromDate,
        DateTime toDate,
        string triggeredBy,
        CancellationToken cancellationToken = default)
    {
        var frequencyLabel = periodKind == PodReportEmailPeriodKind.Weekly ? "Weekly" : "Full Month";
        var fromText = FormatDateForLog(fromDate);
        var toText = FormatDateForLog(toDate);
        var elapsed = Stopwatch.StartNew();
        var options = await GetOptionsAsync();
        if (options.To.Count == 0)
        {
            logger.LogWarning(
                SendFailedEvent,
                "POD report email send skipped. Reason={FailureReason}, PeriodKind={PeriodKind}, FromDate={FromDate}, ToDate={ToDate}, TriggeredBy={TriggeredBy}",
                "MissingToRecipients",
                periodKind,
                fromText,
                toText,
                triggeredBy);
            await LogPodReportEmailFailureAuditAsync(
                periodKind,
                fromDate,
                toDate,
                triggeredBy,
                "MissingToRecipients",
                "Add at least one To recipient before sending POD report emails.");
            return new PodReportEmailSendResult(false, "Add at least one To recipient before sending POD report emails.");
        }

        logger.LogInformation(
            SendStartedEvent,
            "POD report email send started. PeriodKind={PeriodKind}, Frequency={Frequency}, FromDate={FromDate}, ToDate={ToDate}, TriggeredBy={TriggeredBy}, RecipientCount={RecipientCount}, CcCount={CcCount}, ScheduledSendingEnabled={ScheduledSendingEnabled}",
            periodKind,
            frequencyLabel,
            fromText,
            toText,
            triggeredBy,
            options.To.Count,
            options.Cc.Count,
            options.Enabled);

        try
        {
            var report = await GetPodReportAsync(fromDate, toDate, cancellationToken);
            if (report is null)
            {
                logger.LogError(
                    SendFailedEvent,
                    "POD report email send failed before SMTP delivery. Reason={FailureReason}, PeriodKind={PeriodKind}, FromDate={FromDate}, ToDate={ToDate}, TriggeredBy={TriggeredBy}, RecipientCount={RecipientCount}, CcCount={CcCount}, ElapsedMs={ElapsedMs}",
                    "ReportApiFailed",
                    periodKind,
                    fromText,
                    toText,
                    triggeredBy,
                    options.To.Count,
                    options.Cc.Count,
                    elapsed.ElapsedMilliseconds);
                await LogPodReportEmailFailureAuditAsync(
                    periodKind,
                    fromDate,
                    toDate,
                    triggeredBy,
                    "ReportApiFailed",
                    "The POD report could not be generated from the API.");
                return new PodReportEmailSendResult(false, "The POD report could not be generated from the API.");
            }

            var excel = reportExportService.ExportPodUploadStatusToExcel(report);
            var periodLabel = FormatPeriod(fromDate, toDate);
            var subject = $"{frequencyLabel} POD Report - {periodLabel}";
            var fileName = $"pod-report-{periodKind.ToString().ToLowerInvariant()}-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.xlsx";
            var htmlBody = BuildEmailBody(report, frequencyLabel, fromDate, toDate, triggeredBy);

            logger.LogInformation(
                ReportGeneratedEvent,
                "POD report data generated. PeriodKind={PeriodKind}, FromDate={FromDate}, ToDate={ToDate}, TotalInvoices={TotalInvoices}, UploadedCount={UploadedCount}, PendingCount={PendingCount}, ItemCount={ItemCount}",
                periodKind,
                fromText,
                toText,
                report.TotalInvoices,
                report.UploadedCount,
                report.PendingCount,
                report.Items?.Count ?? 0);

            logger.LogInformation(
                DeliveryStartedEvent,
                "POD report email delivery starting. PeriodKind={PeriodKind}, Subject={Subject}, FileName={FileName}, AttachmentBytes={AttachmentBytes}, RecipientCount={RecipientCount}, CcCount={CcCount}, TriggeredBy={TriggeredBy}",
                periodKind,
                subject,
                fileName,
                excel.LongLength,
                options.To.Count,
                options.Cc.Count,
                triggeredBy);

            var emailResult = await emailService.SendEmailWithDiagnosticsAsync(
                options.To,
                options.Cc,
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
                    "POD report email delivery failed. Reason={FailureReason}, EmailFailureStage={EmailFailureStage}, EmailExceptionType={EmailExceptionType}, EmailFailureMessage={EmailFailureMessage}, PeriodKind={PeriodKind}, FromDate={FromDate}, ToDate={ToDate}, TriggeredBy={TriggeredBy}, Subject={Subject}, RecipientCount={RecipientCount}, CcCount={CcCount}, TotalInvoices={TotalInvoices}, UploadedCount={UploadedCount}, PendingCount={PendingCount}, AttachmentBytes={AttachmentBytes}, ElapsedMs={ElapsedMs}",
                    "EmailServiceReturnedFalse",
                    emailResult.FailureStage,
                    emailResult.ExceptionType,
                    emailResult.FailureMessage,
                    periodKind,
                    fromText,
                    toText,
                    triggeredBy,
                    subject,
                    options.To.Count,
                    options.Cc.Count,
                    report.TotalInvoices,
                    report.UploadedCount,
                    report.PendingCount,
                    excel.LongLength,
                    elapsed.ElapsedMilliseconds);
                await LogPodReportEmailFailureAuditAsync(
                    periodKind,
                    fromDate,
                    toDate,
                    triggeredBy,
                    emailResult.FailureStage ?? "EmailDeliveryFailed",
                    emailFailure);
                return new PodReportEmailSendResult(false, $"Failed to send the {frequencyLabel.ToLowerInvariant()} POD report email. {emailFailure}");
            }

            logger.LogInformation(
                SendCompletedEvent,
                "{Frequency} POD report emailed to {Recipients} for {FromDate} - {ToDate}. Triggered by {TriggeredBy}.",
                frequencyLabel,
                string.Join(", ", options.To),
                fromText,
                toText,
                triggeredBy);

            logger.LogInformation(
                SendCompletedEvent,
                "POD report email send completed. PeriodKind={PeriodKind}, FromDate={FromDate}, ToDate={ToDate}, TriggeredBy={TriggeredBy}, RecipientCount={RecipientCount}, CcCount={CcCount}, TotalInvoices={TotalInvoices}, UploadedCount={UploadedCount}, PendingCount={PendingCount}, AttachmentBytes={AttachmentBytes}, ElapsedMs={ElapsedMs}",
                periodKind,
                fromText,
                toText,
                triggeredBy,
                options.To.Count,
                options.Cc.Count,
                report.TotalInvoices,
                report.UploadedCount,
                report.PendingCount,
                excel.LongLength,
                elapsed.ElapsedMilliseconds);

            return new PodReportEmailSendResult(
                true,
                $"{frequencyLabel} POD report sent to {options.To.Count} recipient{(options.To.Count == 1 ? string.Empty : "s")}.",
                report.TotalInvoices,
                report.UploadedCount,
                report.PendingCount);
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
                "Failed to send {PeriodKind} POD report email for {FromDate} - {ToDate}. TriggeredBy={TriggeredBy}, RecipientCount={RecipientCount}, CcCount={CcCount}, ElapsedMs={ElapsedMs}, ExceptionType={ExceptionType}, FailureMessage={FailureMessage}",
                periodKind,
                fromText,
                toText,
                triggeredBy,
                options.To.Count,
                options.Cc.Count,
                elapsed.ElapsedMilliseconds,
                ex.GetType().Name,
                ex.Message);
            await LogPodReportEmailFailureAuditAsync(
                periodKind,
                fromDate,
                toDate,
                triggeredBy,
                ex.GetType().Name,
                ex.Message);
            return new PodReportEmailSendResult(false, $"Failed to send POD report email: {ex.Message}");
        }
    }

    public async Task MarkScheduledSentAsync(
        PodReportEmailPeriodKind periodKind,
        DateTime scheduledUtc,
        CancellationToken cancellationToken = default)
    {
        var key = periodKind == PodReportEmailPeriodKind.Weekly
            ? SettingKeys.PodReportEmailsLastWeeklySentUtc
            : SettingKeys.PodReportEmailsLastMonthlySentUtc;

        await settingsService.SaveSettingAsync(
            key,
            scheduledUtc.ToString("O", CultureInfo.InvariantCulture),
            "System");
    }

    public static (DateTime fromDate, DateTime toDate) GetPreviousSevenDayPeriod(DateTime utcNow)
    {
        var toDate = utcNow.Date.AddDays(-1);
        var fromDate = toDate.AddDays(-6);
        return (fromDate, toDate);
    }

    public static (DateTime fromDate, DateTime toDate) GetPreviousCalendarMonthPeriod(DateTime utcNow)
    {
        var monthStart = new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var fromDate = monthStart.AddMonths(-1);
        var toDate = monthStart.AddDays(-1);
        return (fromDate, toDate);
    }

    private async Task<PodUploadStatusReport?> GetPodReportAsync(
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("ShopInventoryApi");
        var from = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var requestPath = $"api/invoice/pod-upload-status?fromDate={from}&toDate={to}";

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
            return null;
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
            return null;
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

        return report;
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

        return $@"
            <h2>{frequencyLabel} POD Report</h2>
            <p>The POD upload report for <strong>{period}</strong> is attached as an Excel workbook.</p>
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
            <p style='color: #666; font-size: 13px; margin-top: 16px;'>Triggered by {triggeredBy}.</p>";
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
        PodReportEmailPeriodKind periodKind,
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
                    $"Period={periodKind}",
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
                periodKind.ToString(),
                details,
                "/settings",
                isSuccess: false,
                errorMessage: TruncateForLog(failureMessage, 1000));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to write POD report email failure audit entry. PeriodKind={PeriodKind}, FailureStage={FailureStage}",
                periodKind,
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
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ParseBool(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

    private static int ParseHour(string? value, int fallback)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            parsed = fallback;
        }

        return Math.Clamp(parsed, 0, 23);
    }

    private static int ParseDayOfMonth(string? value, int fallback)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            parsed = fallback;
        }

        return Math.Clamp(parsed, 1, 31);
    }

    private static DayOfWeek ParseDayOfWeek(string? value, DayOfWeek fallback) =>
        Enum.TryParse<DayOfWeek>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    private static DateTime? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.Kind == DateTimeKind.Utc
                ? parsed
                : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return null;
    }
}
