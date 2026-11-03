using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppReport;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;
using ShopInventory.Models;
using ShopInventory.Services;
using Keys = ShopInventory.Features.DailySalesWhatsApp.DailySalesWhatsAppKeys;

namespace ShopInventory.Features.DailySalesWhatsApp.Commands.SendDailySalesWhatsApp;

/// <summary>
/// Reads the day's report and sends it to each number in turn, from the number customer documents are
/// sent from.
/// </summary>
/// <remarks>
/// <para>
/// One message per number, so one refused number does not cost everyone else their copy. Every number
/// that has the report is remembered against the day, so the job — which runs every quarter hour from
/// 07:15 to 09:45 — sends only to the ones still missing it. That is what carries the report over a
/// gateway that is down at 07:15, or a pass landing on a node with no WhatsApp settings of its own.
/// </para>
/// <para>
/// A send WhatsApp may well have delivered — no answer in time, or accepted without a message id — is
/// remembered as sent. A second copy of the morning's figures is a small harm, but it is the gateway's
/// own rule (<see cref="OpenWADispatchOutcomeClassifier"/>) never to resend on that alone, and this
/// follows it.
/// </para>
/// </remarks>
public sealed class SendDailySalesWhatsAppHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IOpenWAClient openWaClient,
    IOptions<OpenWASettings> openWaSettings,
    IOptions<CustomerDocumentDeliverySettings> documentSettings,
    IAuditService auditService,
    ILogger<SendDailySalesWhatsAppHandler> logger,
    TimeProvider? clock = null)
    : IRequestHandler<SendDailySalesWhatsAppCommand, ErrorOr<DailySalesWhatsAppSendResult>>
{
    public async Task<ErrorOr<DailySalesWhatsAppSendResult>> Handle(
        SendDailySalesWhatsAppCommand request, CancellationToken cancellationToken)
    {
        var today = AuditService.ToCAT((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime).Date;
        var day = request.Scheduled ? today.AddDays(-1) : (request.TradingDate?.Date ?? today.AddDays(-1));
        if (day > today)
        {
            return Errors.DailySalesWhatsApp.FutureDay;
        }

        var settings = await mediator.Send(new GetDailySalesWhatsAppSettingsQuery(), cancellationToken);
        if (settings.IsError)
        {
            return settings.Errors;
        }

        if (request.Scheduled && !settings.Value.Enabled)
        {
            return Skipped(day, "the report is switched off");
        }

        var recipients = settings.Value.Recipients;
        if (!request.Scheduled && request.Recipients?.Any(number => !string.IsNullOrWhiteSpace(number)) == true)
        {
            recipients = [];
            foreach (var typed in request.Recipients.Where(number => !string.IsNullOrWhiteSpace(number)))
            {
                if (!WhatsAppRecipients.TryNormalise(typed, documentSettings.Value.DefaultCountryCode, out var e164))
                {
                    return Errors.DailySalesWhatsApp.InvalidNumber(typed.Trim());
                }

                if (!recipients.Contains(e164, StringComparer.Ordinal))
                {
                    recipients.Add(e164);
                }
            }
        }

        if (recipients.Count == 0)
        {
            return Errors.DailySalesWhatsApp.NoRecipients;
        }

        var alreadySent = settings.Value.LastSentFor == day ? settings.Value.LastSentTo : [];
        if (request.Scheduled)
        {
            recipients = recipients.Where(number => !alreadySent.Contains(number, StringComparer.Ordinal)).ToList();
            if (recipients.Count == 0)
            {
                return Skipped(day, "every number already has this day's report");
            }
        }

        if (!WhatsAppGateway.IsConfigured(openWaSettings.Value))
        {
            // A scheduled pass on a node without the gateway leaves the send to the next pass, which may
            // land on a node that has it.
            return request.Scheduled
                ? Skipped(day, "WhatsApp is not set up on this server")
                : Errors.DailySalesWhatsApp.GatewayNotConfigured;
        }

        var runtime = await CustomerDocumentDeliveryKeys.ReadAsync(db, cancellationToken);
        var session = await CustomerDocumentSession.EnsureAsync(
            db, openWaClient, openWaSettings.Value, documentSettings.Value, runtime, logger, cancellationToken);
        if (session.Kind != SendingSessionKind.Chosen || session.SessionId is not { } sessionId)
        {
            var why = session.Refusal(documentSettings.Value.PreferredSessionName)?.Description
                      ?? "WhatsApp did not answer when asked which number sends messages.";
            return request.Scheduled ? Skipped(day, why) : Errors.DailySalesWhatsApp.NoSendingNumber(why);
        }

        var report = await mediator.Send(new GetDailySalesWhatsAppReportQuery(day), cancellationToken);
        if (report.IsError)
        {
            return report.Errors;
        }

        var sendingPhone = await CustomerDocumentSession.SendingPhoneAsync(
            openWaClient, openWaSettings.Value, sessionId, logger, cancellationToken);

        var sent = new List<string>();
        var unconfirmed = new List<string>();
        var failed = new List<DailySalesWhatsAppSendFailure>();

        foreach (var number in recipients)
        {
            if (WhatsAppRecipients.IsSendingNumber(number, sendingPhone))
            {
                failed.Add(new DailySalesWhatsAppSendFailure(number, Errors.DailySalesWhatsApp.SendingNumber(number).Description));
                continue;
            }

            OpenWADispatchOutcome outcome;
            try
            {
                var dispatch = await openWaClient.SendTextAsync(
                    sessionId,
                    new WhatsAppSendTextRequestDto { ChatId = WhatsAppRecipients.ChatId(number), Text = report.Value.Message },
                    cancellationToken);
                outcome = OpenWADispatchOutcomeClassifier.Classify(dispatch);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = OpenWADispatchOutcomeClassifier.Classify(ex);
            }

            switch (outcome.Kind)
            {
                case OpenWADispatchOutcomeKind.Sent:
                    sent.Add(number);
                    break;
                case OpenWADispatchOutcomeKind.SentUnconfirmed or OpenWADispatchOutcomeKind.Uncertain:
                    unconfirmed.Add(number);
                    break;
                default:
                    failed.Add(new DailySalesWhatsAppSendFailure(number, outcome.Error ?? "WhatsApp did not send it."));
                    break;
            }
        }

        var delivered = sent.Concat(unconfirmed).ToList();

        // Only yesterday's sends are remembered, because only yesterday is what the morning pass sends. A
        // person's send of yesterday counts too, so one at 07:00 is not repeated at 07:15; a resend of an
        // older day must not wipe out the record of who already has yesterday's.
        if (delivered.Count > 0 && day == today.AddDays(-1))
        {
            var remembered = alreadySent.Concat(delivered).Distinct(StringComparer.Ordinal).ToList();
            await Keys.StageAsync(db, Keys.SentFor, "date", Keys.DateValue(day),
                "The trading day the daily sales WhatsApp report was last sent for.", cancellationToken);
            await Keys.StageAsync(db, Keys.SentTo, "string", string.Join(",", remembered),
                "The numbers that have the daily sales WhatsApp report for that day.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Daily sales WhatsApp report for {Day:yyyy-MM-dd} {How}: sent {Sent}, unconfirmed {Unconfirmed}, failed {Failed}",
            day, request.Scheduled ? "on schedule" : $"by {request.CallerName}", sent.Count, unconfirmed.Count, failed.Count);
        foreach (var failure in failed)
        {
            logger.LogWarning("Daily sales WhatsApp report for {Day:yyyy-MM-dd} not sent to {Number}: {Reason}",
                day, WhatsAppRecipients.Mask(failure.Number), failure.Reason);
        }

        await AuditAsync(request, day, delivered, failed);

        return new DailySalesWhatsAppSendResult(day, sent, unconfirmed, failed, false, null, report.Value.Message);
    }

    private DailySalesWhatsAppSendResult Skipped(DateTime day, string reason)
    {
        logger.LogInformation("Daily sales WhatsApp report for {Day:yyyy-MM-dd} not sent: {Reason}", day, reason);
        return new DailySalesWhatsAppSendResult(day, [], [], [], true, reason, null);
    }

    private async Task AuditAsync(
        SendDailySalesWhatsAppCommand request, DateTime day, List<string> delivered, List<DailySalesWhatsAppSendFailure> failed)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.SendDailySalesWhatsApp,
                "DailySalesWhatsApp",
                Keys.DateValue(day),
                $"Daily sales WhatsApp report for {Keys.DateValue(day)} "
                    + (request.Scheduled ? "sent on schedule" : $"sent by {request.CallerName ?? "an admin"}")
                    + $" to {(delivered.Count == 0 ? "nobody" : string.Join(", ", delivered.Select(WhatsAppRecipients.Mask)))}"
                    + (failed.Count == 0 ? string.Empty : $"; not sent to {string.Join(", ", failed.Select(failure => WhatsAppRecipients.Mask(failure.Number)))}"),
                delivered.Count > 0,
                delivered.Count > 0 ? null : "WhatsApp accepted the report for none of the numbers.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the daily sales WhatsApp send");
        }
    }
}
