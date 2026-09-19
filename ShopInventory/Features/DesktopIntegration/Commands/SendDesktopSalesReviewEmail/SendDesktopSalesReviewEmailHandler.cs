using System.Globalization;
using ErrorOr;
using MediatR;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewPdf;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;
using ShopInventory.Models;
using ShopInventory.Services;
using static ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule.DesktopSalesReviewScheduleKeys;

namespace ShopInventory.Features.DesktopIntegration.Commands.SendDesktopSalesReviewEmail;

/// <summary>
/// Renders the review and mails it to each recipient in turn.
/// </summary>
/// <remarks>
/// <para>
/// One message per recipient rather than one to many, so one bad address does not lose everyone else
/// their copy, and the result can say exactly who did not get it.
/// </para>
/// <para>
/// A scheduled send remembers the period it sent. The job runs every morning, so a Monday the server was
/// down is caught up on Tuesday — but only for <see cref="CatchUpDays"/> days, so switching the email on
/// in mid-week does not suddenly mail an old week.
/// </para>
/// </remarks>
public sealed class SendDesktopSalesReviewEmailHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IEmailService emailService,
    IAuditService auditService,
    ILogger<SendDesktopSalesReviewEmailHandler> logger,
    TimeProvider? clock = null)
    : IRequestHandler<SendDesktopSalesReviewEmailCommand, ErrorOr<DesktopSalesReviewEmailResult>>
{
    /// <summary>How many days after a period ends a missed scheduled send may still go out.</summary>
    public const int CatchUpDays = 3;

    public async Task<ErrorOr<DesktopSalesReviewEmailResult>> Handle(
        SendDesktopSalesReviewEmailCommand request, CancellationToken cancellationToken)
    {
        var schedule = await mediator.Send(new GetDesktopSalesReviewScheduleQuery(), cancellationToken);
        if (schedule.IsError)
        {
            return schedule.Errors;
        }

        var today = AuditService.ToCAT((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime).Date;
        var (from, to) = request.Cadence == DesktopSalesReviewCadence.Custom
            ? (request.FromDate!.Value.Date, (request.ToDate ?? request.FromDate!.Value).Date)
            : DesktopSalesReviewCadence.LastComplete(request.Cadence, today);

        if (request.Scheduled)
        {
            var reason = WhyNot(request.Cadence, schedule.Value, today, to);
            if (reason is not null)
            {
                logger.LogInformation("Desktop sales review {Cadence} for {To:yyyy-MM-dd} not sent: {Reason}", request.Cadence, to, reason);
                return new DesktopSalesReviewEmailResult(request.Cadence, from, to, [], [], true, reason, 0);
            }
        }

        var caller = request.Scheduled ? schedule.Value.SendAsUserId : request.CallerUserId;
        if (caller is null)
        {
            return Common.Errors.Errors.DesktopSalesReview.NotScheduled;
        }

        var recipients = request.Recipients is { Count: > 0 } named
            ? Addresses(string.Join(',', named))
            : schedule.Value.Recipients;
        if (recipients.Count == 0)
        {
            return Common.Errors.Errors.DesktopSalesReview.NoRecipients;
        }

        var title = DesktopSalesReviewCadence.Title(request.Cadence);
        var document = await mediator.Send(
            new GetDesktopSalesReviewPdfQuery(caller.Value, from, to, Title: title),
            cancellationToken);
        if (document.IsError)
        {
            return document.Errors;
        }

        var review = document.Value.Review;
        var subject = $"{title}: {Day(review.FromDate)} to {Day(review.ToDate)} {review.ToDate:yyyy}";
        var body = DesktopSalesReviewEmailBody.Build(review, title, AuditService.ToCAT(review.GeneratedAtUtc));
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
            await StageAsync(db, LastSent(request.Cadence), "date", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                $"The last {request.Cadence} period end the desktop sales review was emailed for.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await AuditAsync(request, from, to, sent, failed);

        if (sent.Count == 0)
        {
            logger.LogWarning("Desktop sales review {Cadence} for {From:yyyy-MM-dd}..{To:yyyy-MM-dd} reached nobody: {Failed}",
                request.Cadence, from, to, string.Join(", ", failed));
            return Common.Errors.Errors.DesktopSalesReview.EmailDisabled;
        }

        logger.LogInformation("Desktop sales review {Cadence} for {From:yyyy-MM-dd}..{To:yyyy-MM-dd} sent to {Sent}; failed {Failed}",
            request.Cadence, from, to, sent.Count, failed.Count);

        return new DesktopSalesReviewEmailResult(request.Cadence, from, to, sent, failed, false, null, review.Businesses.Sum(b => b.Findings.Count));
    }

    /// <summary>Why a scheduled send should not go out, or null when it should.</summary>
    private static string? WhyNot(string cadence, DesktopSalesReviewSchedule schedule, DateTime today, DateTime periodEnd)
    {
        var enabled = cadence switch
        {
            DesktopSalesReviewCadence.Weekly => schedule.WeeklyEnabled,
            DesktopSalesReviewCadence.Monthly => schedule.MonthlyEnabled,
            _ => false
        };
        if (!enabled)
        {
            return $"the {cadence} review is switched off";
        }

        var lastSent = cadence == DesktopSalesReviewCadence.Weekly ? schedule.LastWeeklyPeriodEnd : schedule.LastMonthlyPeriodEnd;
        if (lastSent == periodEnd)
        {
            return "this period has already been sent";
        }

        return (today - periodEnd).Days > CatchUpDays
            ? $"the period ended more than {CatchUpDays} days ago"
            : null;
    }

    private async Task AuditAsync(SendDesktopSalesReviewEmailCommand request, DateTime from, DateTime to, List<string> sent, List<string> failed)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.SendDesktopSalesReview,
                "DesktopSalesReview",
                $"{request.Cadence}:{to:yyyy-MM-dd}",
                $"{DesktopSalesReviewCadence.Title(request.Cadence)} for {from:yyyy-MM-dd} to {to:yyyy-MM-dd} "
                    + (request.Scheduled ? "sent on schedule" : $"sent by {request.CallerName ?? request.CallerUserId?.ToString()}")
                    + $" to {(sent.Count == 0 ? "nobody" : string.Join(", ", sent))}"
                    + (failed.Count == 0 ? string.Empty : $"; not delivered to {string.Join(", ", failed)}"),
                sent.Count > 0,
                sent.Count > 0 ? null : "No recipient accepted the email.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the desktop sales review send");
        }
    }

    private static string Day(DateTime date) => date.ToString("d MMM", CultureInfo.InvariantCulture);
}
