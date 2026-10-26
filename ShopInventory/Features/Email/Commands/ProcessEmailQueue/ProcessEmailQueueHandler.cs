using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Email.Commands.ProcessEmailQueue;

/// <summary>
/// Sends what is waiting in the email queue, a few at a time. Run every minute by
/// <see cref="EmailQueueJob"/> and on demand by <c>POST api/email/process-queue</c>.
/// </summary>
/// <remarks>
/// Each row is claimed with a conditional update before it is sent, so the job and an on-demand call
/// running at once never both send it. A row whose pass died mid-send is found by its claim time and
/// tried again. Does nothing while <c>Email:Enabled</c> is off, so rows wait for email rather than using
/// up their attempts on "disabled".
/// </remarks>
public sealed class ProcessEmailQueueHandler(
    ApplicationDbContext context,
    IEmailService emailService,
    IOptions<EmailSettings> emailSettings,
    TimeProvider timeProvider,
    ILogger<ProcessEmailQueueHandler> logger
) : IRequestHandler<ProcessEmailQueueCommand, ErrorOr<ProcessEmailQueueResult>>
{
    internal const int MaxAttempts = 3;
    private const int BatchSize = 10;
    private const int MaxErrorLength = 1000;

    /// <summary>Well past the SMTP client's own timeouts, so only a pass that died is still Sending.</summary>
    internal static readonly TimeSpan StaleSendingAfter = TimeSpan.FromMinutes(15);

    /// <summary>A password reset token lasts 24 hours; an email sent later than that is no use.</summary>
    internal static readonly TimeSpan ExpireAfter = TimeSpan.FromHours(24);

    public async Task<ErrorOr<ProcessEmailQueueResult>> Handle(
        ProcessEmailQueueCommand command,
        CancellationToken cancellationToken)
    {
        if (!emailSettings.Value.Enabled)
        {
            logger.LogDebug("Email is switched off; the email queue waits.");
            return ProcessEmailQueueResult.Nothing;
        }

        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;

            await RecoverCutOffSendsAsync(now, cancellationToken);
            var expired = await ExpireStaleAsync(now, cancellationToken);

            var ids = await context.EmailQueueItems
                .AsNoTracking()
                .Where(e => e.Status == EmailQueueStatus.Pending && e.AttemptCount < MaxAttempts)
                .OrderBy(e => e.CreatedAt)
                .ThenBy(e => e.Id)
                .Select(e => e.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            int sent = 0, retrying = 0, failed = 0;
            foreach (var id in ids)
            {
                switch (await SendOneAsync(id, now, cancellationToken))
                {
                    case RowOutcome.Sent: sent++; break;
                    case RowOutcome.Retrying: retrying++; break;
                    case RowOutcome.Failed: failed++; break;
                }
            }

            // The logs carry no figure read back from EmailQueueItems: CodeQL treats anything from that
            // table as private (it holds addresses), row counts included. process-queue returns them all.
            if (expired > 0)
            {
                logger.LogWarning("Queued emails more than a day old were dropped unsent (Status Failed).");
            }

            if (sent + retrying + failed > 0)
            {
                logger.LogInformation(
                    "Email queue pass: {Sent} sent, {Retrying} to retry, {Failed} given up",
                    sent, retrying, failed);
            }

            return new ProcessEmailQueueResult(sent, retrying, failed, expired);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error processing email queue");
            return Errors.Email.ProcessingFailed(ex.Message);
        }
    }

    private async Task<RowOutcome> SendOneAsync(int id, DateTime now, CancellationToken cancellationToken)
    {
        var claimed = await context.EmailQueueItems
            .Where(e => e.Id == id && e.Status == EmailQueueStatus.Pending && e.AttemptCount < MaxAttempts)
            .ExecuteUpdateAsync(update => update
                .SetProperty(e => e.Status, EmailQueueStatus.Sending)
                .SetProperty(e => e.AttemptCount, e => e.AttemptCount + 1)
                .SetProperty(e => e.LastAttemptAt, now),
                cancellationToken);

        if (claimed == 0)
            return RowOutcome.Skipped;

        // ExecuteUpdate never touches the change tracker; a copy tracked earlier would be stale.
        context.ChangeTracker.Clear();
        var row = await context.EmailQueueItems.SingleAsync(e => e.Id == id, cancellationToken);

        string? error;
        try
        {
            var result = await emailService.SendEmailAsync(new SendEmailRequest
            {
                To = EmailQueueAddresses.Parse(row.ToAddresses),
                Cc = NullIfEmpty(EmailQueueAddresses.Parse(row.CcAddresses)),
                Bcc = NullIfEmpty(EmailQueueAddresses.Parse(row.BccAddresses)),
                Subject = row.Subject,
                Body = row.Body,
                IsHtml = row.IsHtml
            }, cancellationToken);

            error = result.Success ? null : result.Message ?? "The email service refused the email.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A row that cannot even be read (a malformed address list) lands here too, and is given
            // up after its attempts like any other failure instead of blocking the queue.
            logger.LogError(ex, "Sending queued email {EmailId} failed", row.Id);
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        RowOutcome outcome;
        if (error is null)
        {
            row.Status = EmailQueueStatus.Sent;
            row.SentAt = now;
            row.LastError = null;
            outcome = RowOutcome.Sent;
        }
        else
        {
            var givenUp = row.AttemptCount >= MaxAttempts;
            row.Status = givenUp ? EmailQueueStatus.Failed : EmailQueueStatus.Pending;
            row.LastError = Truncate(error);
            outcome = givenUp ? RowOutcome.Failed : RowOutcome.Retrying;
            logger.LogWarning(
                "Queued email {EmailId} not sent (attempt {Attempt} of {MaxAttempts}): {Error}",
                row.Id, row.AttemptCount, MaxAttempts, error);
        }

        await context.SaveChangesAsync(cancellationToken);
        return outcome;
    }

    /// <summary>
    /// A row still Sending long after its claim belongs to a pass that died. Rows the old drain left
    /// there have no claim time, so their queue time stands in.
    /// </summary>
    private async Task RecoverCutOffSendsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutOff = now - StaleSendingAfter;
        var stale = context.EmailQueueItems
            .Where(e => e.Status == EmailQueueStatus.Sending && (e.LastAttemptAt ?? e.CreatedAt) < cutOff);

        var retried = await stale
            .Where(e => e.AttemptCount < MaxAttempts)
            .ExecuteUpdateAsync(update => update
                .SetProperty(e => e.Status, EmailQueueStatus.Pending)
                .SetProperty(e => e.LastError, "Cut off mid-send; trying again."),
                cancellationToken);

        // Whatever is still stale had used its last attempt.
        var givenUp = await stale
            .ExecuteUpdateAsync(update => update
                .SetProperty(e => e.Status, EmailQueueStatus.Failed)
                .SetProperty(e => e.LastError, "Cut off mid-send on its last attempt."),
                cancellationToken);

        if (retried > 0)
        {
            logger.LogWarning("Queued emails cut off mid-send were put back to be tried again.");
        }

        if (givenUp > 0)
        {
            logger.LogWarning("Queued emails cut off mid-send on their last attempt were given up (Status Failed).");
        }
    }

    private Task<int> ExpireStaleAsync(DateTime now, CancellationToken cancellationToken)
    {
        var tooOld = now - ExpireAfter;
        return context.EmailQueueItems
            .Where(e => e.Status == EmailQueueStatus.Pending && e.CreatedAt < tooOld)
            .ExecuteUpdateAsync(update => update
                .SetProperty(e => e.Status, EmailQueueStatus.Failed)
                .SetProperty(e => e.LastError, "Not sent within 24 hours of being queued; dropped as stale."),
                cancellationToken);
    }

    private static List<string>? NullIfEmpty(List<string> addresses) => addresses.Count == 0 ? null : addresses;

    private static string Truncate(string error) =>
        error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];

    private enum RowOutcome { Skipped, Sent, Retrying, Failed }
}
