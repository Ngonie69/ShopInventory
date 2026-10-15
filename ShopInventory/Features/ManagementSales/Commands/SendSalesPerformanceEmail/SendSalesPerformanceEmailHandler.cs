using System.Globalization;
using ErrorOr;
using MediatR;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewPdf;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;
using ShopInventory.Features.ManagementSales.Queries.GetSalesPerformance;
using ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;
using ShopInventory.Models;
using ShopInventory.Services;
using static ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule.DesktopSalesReviewScheduleKeys;
using Keys = ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule.SalesPerformanceEmailScheduleKeys;

namespace ShopInventory.Features.ManagementSales.Commands.SendSalesPerformanceEmail;

/// <summary>
/// Reads shops, vending and the vans for the period and mails the result to each recipient in turn.
/// </summary>
/// <remarks>
/// <para>
/// One message per recipient rather than one to many, so one bad address does not lose everyone else
/// their copy, and the result can say exactly who did not get it — as the Shop &amp; Vending review does.
/// </para>
/// <para>
/// A scheduled send remembers the period it sent. The job runs every morning, so a Monday the server was
/// down is caught up on Tuesday — but only for <see cref="CatchUpDays"/> days, so switching the email on
/// mid-week does not suddenly mail an old week. A daily email always covers yesterday, so a missed day
/// is simply not sent.
/// </para>
/// </remarks>
public sealed class SendSalesPerformanceEmailHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IEmailService emailService,
    IAuditService auditService,
    ILogger<SendSalesPerformanceEmailHandler> logger,
    TimeProvider? clock = null)
    : IRequestHandler<SendSalesPerformanceEmailCommand, ErrorOr<SalesPerformanceEmailResult>>
{
    /// <summary>How many days after a period ends a missed scheduled send may still go out.</summary>
    public const int CatchUpDays = 3;

    public async Task<ErrorOr<SalesPerformanceEmailResult>> Handle(
        SendSalesPerformanceEmailCommand request, CancellationToken cancellationToken)
    {
        if (!SalesPerformanceEmailCadence.IsKnown(request.Cadence))
        {
            return Common.Errors.Errors.SalesPerformanceEmail.UnknownCadence(request.Cadence);
        }

        var schedule = await mediator.Send(new GetSalesPerformanceEmailScheduleQuery(), cancellationToken);
        if (schedule.IsError)
        {
            return schedule.Errors;
        }

        var today = AuditService.ToCAT((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime).Date;
        var (from, to) = request.Cadence == SalesPerformanceEmailCadence.Custom
            ? (request.FromDate!.Value.Date, (request.ToDate ?? request.FromDate!.Value).Date)
            : SalesPerformanceEmailCadence.LastComplete(request.Cadence, today);

        if (request.Scheduled)
        {
            var reason = WhyNot(request.Cadence, schedule.Value, today, to);
            if (reason is not null)
            {
                logger.LogInformation("Sales performance email {Cadence} for {To:yyyy-MM-dd} not sent: {Reason}", request.Cadence, to, reason);
                return new SalesPerformanceEmailResult(request.Cadence, from, to, [], [], true, reason, 0);
            }
        }

        var caller = request.Scheduled ? schedule.Value.SendAsUserId : request.CallerUserId;
        if (caller is null)
        {
            return Common.Errors.Errors.SalesPerformanceEmail.NotScheduled;
        }

        var recipients = request.Recipients is { Count: > 0 } named
            ? Addresses(string.Join(',', named))
            : schedule.Value.Recipients;
        if (recipients.Count == 0)
        {
            return Common.Errors.Errors.SalesPerformanceEmail.NoRecipients;
        }

        // The review is read once, with its PDF, and the figures are taken from that same read.
        var document = await mediator.Send(
            new GetDesktopSalesReviewPdfQuery(caller.Value, from, to, Title: ReviewTitle(request.Cadence)),
            cancellationToken);
        if (document.IsError)
        {
            return document.Errors;
        }

        var performance = await SalesPerformanceReader.ForReviewAsync(mediator, db, document.Value.Review, cancellationToken);
        if (performance.IsError)
        {
            return performance.Errors;
        }

        var figures = performance.Value;
        var title = SalesPerformanceEmailCadence.Title(request.Cadence);
        var subject = figures.FromDate == figures.ToDate
            ? $"{title}: {figures.FromDate.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}"
            : $"{title}: {Day(figures.FromDate)} to {Day(figures.ToDate)} {figures.ToDate:yyyy}";
        var body = SalesPerformanceEmailBody.Build(
            figures, title, SalesPerformanceEmailCadence.PreviousLabel(request.Cadence), AuditService.ToCAT(figures.GeneratedAtUtc));
        var attachment = new List<(byte[] content, string fileName, string mimeType)>
        {
            (document.Value.Content, document.Value.FileName, "application/pdf")
        };

        var sent = new List<string>();
        var failed = new List<string>();
        foreach (var recipient in recipients)
        {
            var delivered = await emailService.SendEmailAsync(recipient, subject, body, attachment, null, cancellationToken);
            (delivered ? sent : failed).Add(recipient);
        }

        if (request.Scheduled && sent.Count > 0)
        {
            await Keys.StageAsync(db, Keys.LastSent(request.Cadence), "date", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                $"The last {request.Cadence} period end the management sales performance email was sent for.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await AuditAsync(request, from, to, sent, failed);

        if (sent.Count == 0)
        {
            logger.LogWarning("Sales performance email {Cadence} for {From:yyyy-MM-dd}..{To:yyyy-MM-dd} reached nobody: {Failed}",
                request.Cadence, from, to, string.Join(", ", failed));
            return Common.Errors.Errors.SalesPerformanceEmail.EmailDisabled;
        }

        logger.LogInformation("Sales performance email {Cadence} for {From:yyyy-MM-dd}..{To:yyyy-MM-dd} sent to {Sent}; failed {Failed}",
            request.Cadence, from, to, sent.Count, failed.Count);

        var findings = (figures.Shops?.Findings.Count ?? 0) + (figures.Vending?.Findings.Count ?? 0) + figures.Vans.Findings.Count;
        return new SalesPerformanceEmailResult(request.Cadence, from, to, sent, failed, false, null, findings);
    }

    /// <summary>Why a scheduled send should not go out, or null when it should.</summary>
    private static string? WhyNot(string cadence, SalesPerformanceEmailSchedule schedule, DateTime today, DateTime periodEnd)
    {
        var (enabled, lastSent) = cadence switch
        {
            SalesPerformanceEmailCadence.Daily => (schedule.DailyEnabled, schedule.LastDailyPeriodEnd),
            SalesPerformanceEmailCadence.Weekly => (schedule.WeeklyEnabled, schedule.LastWeeklyPeriodEnd),
            SalesPerformanceEmailCadence.Monthly => (schedule.MonthlyEnabled, schedule.LastMonthlyPeriodEnd),
            _ => (false, null)
        };

        if (!enabled)
        {
            return $"the {cadence} email is switched off";
        }

        if (lastSent == periodEnd)
        {
            return "this period has already been sent";
        }

        return (today - periodEnd).Days > CatchUpDays
            ? $"the period ended more than {CatchUpDays} days ago"
            : null;
    }

    /// <summary>What the attached review calls itself: the review's own weekly and monthly titles.</summary>
    private static string ReviewTitle(string cadence) => cadence switch
    {
        SalesPerformanceEmailCadence.Weekly or SalesPerformanceEmailCadence.Monthly => DesktopSalesReviewCadence.Title(cadence),
        SalesPerformanceEmailCadence.Daily => "Daily sales review",
        _ => DesktopSalesReviewCadence.Title(DesktopSalesReviewCadence.Custom)
    };

    private async Task AuditAsync(SendSalesPerformanceEmailCommand request, DateTime from, DateTime to, List<string> sent, List<string> failed)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.SendSalesPerformanceEmail,
                "SalesPerformanceEmail",
                $"{request.Cadence}:{to:yyyy-MM-dd}",
                $"{SalesPerformanceEmailCadence.Title(request.Cadence)} for {from:yyyy-MM-dd} to {to:yyyy-MM-dd} "
                    + (request.Scheduled ? "sent on schedule" : $"sent by {request.CallerName ?? request.CallerUserId?.ToString()}")
                    + $" to {(sent.Count == 0 ? "nobody" : string.Join(", ", sent))}"
                    + (failed.Count == 0 ? string.Empty : $"; not delivered to {string.Join(", ", failed)}"),
                sent.Count > 0,
                sent.Count > 0 ? null : "No recipient accepted the email.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the sales performance email send");
        }
    }

    private static string Day(DateTime date) => date.ToString("d MMM", CultureInfo.InvariantCulture);
}
