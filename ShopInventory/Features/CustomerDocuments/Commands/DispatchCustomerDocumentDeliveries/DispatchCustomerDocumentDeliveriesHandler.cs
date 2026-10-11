using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Features.CustomerDocuments.Documents;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.DispatchCustomerDocumentDeliveries;

/// <summary>
/// One pass of the delivery job: the only code that sends a customer document.
/// </summary>
/// <remarks>
/// <para>
/// A WhatsApp message cannot be taken back, so the pass is built around never sending one twice:
/// </para>
/// <list type="bullet">
/// <item>A row is claimed with a conditional update before anything is done with it, so two passes —
/// on two nodes, or a manual trigger beside the schedule — cannot both take it. The job also allows no
/// concurrent run.</item>
/// <item><see cref="CustomerDocumentDeliveryStatus.Sending"/> is saved before the gateway is called. A
/// pass that dies mid-send leaves a row that says so, which a later pass turns into
/// <see cref="CustomerDocumentDeliveryStatus.Uncertain"/> rather than sending again.</item>
/// <item>Only a send that provably never left is retried. Anything that may have arrived is settled
/// from the gateway's own log (<see cref="UncertainDeliveryReconciler"/>).</item>
/// <item>Once Sending is saved, the call and the save after it run on their own deadline rather than
/// the job's token: a deploy that stops the job must not cut off the record of a send already made.
/// No new send starts once the application is stopping.</item>
/// </list>
/// <para>
/// And it is built around keeping the number in good standing with WhatsApp: a gap with a random part
/// between sends, hourly and daily caps across the cluster, a per-number cap, a window and a separate
/// cap for automatic sends, and a daily cap on asking WhatsApp whether numbers exist.
/// </para>
/// <para>
/// A pass on a node without the gateway configured, or with no session to send from, does nothing at
/// all — it claims nothing, so a node that cannot send never strands a row another node could have
/// sent. Nobody has to choose that session: when none is saved the pass picks the gateway's ready one
/// and saves it (<see cref="CustomerDocumentSession"/>), and only tells an administrator when it cannot.
/// </para>
/// </remarks>
public sealed class DispatchCustomerDocumentDeliveriesHandler(
    ApplicationDbContext context,
    IOpenWAClient openWaClient,
    IOptions<OpenWASettings> openWaOptions,
    IOptions<CustomerDocumentDeliverySettings> options,
    ISapInvoiceDocumentComposer sapInvoiceComposer,
    ICustomerDocumentAlerts alerts,
    ICustomerDocumentPacer pacer,
    IHostApplicationLifetime lifetime,
    ILogger<DispatchCustomerDocumentDeliveriesHandler> logger)
    : IRequestHandler<DispatchCustomerDocumentDeliveriesCommand, ErrorOr<DispatchCustomerDocumentDeliveriesResult>>
{
    private const string ReadySessionStatus = "ready";
    private const string HeldAlert = "Held";
    private const string UncertainAlert = "Uncertain";
    private const string BacklogAlert = "Backlog";
    private const string CapReachedAlert = "CapReached";
    private const string NoSessionAlert = "NoSession";

    private static readonly TimeSpan PrepareRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PausedRetryDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReconcileWindow = TimeSpan.FromHours(24);

    private static long _lastUnconfiguredWarningTicks;

    private bool Stopping => lifetime.ApplicationStopping.IsCancellationRequested;

    public async Task<ErrorOr<DispatchCustomerDocumentDeliveriesResult>> Handle(
        DispatchCustomerDocumentDeliveriesCommand command,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (!settings.Enabled)
            return DispatchCustomerDocumentDeliveriesResult.Idle("Sending customer documents is switched off.");

        if (!WhatsAppGateway.IsConfigured(openWaOptions.Value))
        {
            WarnUnconfigured();
            return DispatchCustomerDocumentDeliveriesResult.Idle("WhatsApp is not configured on this node.");
        }

        var runtime = await CustomerDocumentDeliveryKeys.ReadAsync(context, cancellationToken);
        var sending = await CustomerDocumentSession.EnsureAsync(
            context, openWaClient, openWaOptions.Value, settings, runtime, logger, cancellationToken);
        if (sending.SessionId is not { } sessionId)
            return await IdleWithoutSessionAsync(sending, cancellationToken);

        if (Stopping)
            return DispatchCustomerDocumentDeliveriesResult.Idle("The application is stopping.");

        var now = pacer.UtcNow;
        await HousekeepAsync(now, cancellationToken);
        var reconciled = await ReconcileUncertainAsync(sessionId, now, cancellationToken);

        var sessionStatus = await ReadSessionStatusAsync(sessionId, cancellationToken);
        if (!string.Equals(sessionStatus, ReadySessionStatus, StringComparison.OrdinalIgnoreCase))
        {
            await alerts.RaiseAsync(
                OpenWADispatchOutcomeClassifier.SessionDownAlert,
                "WhatsApp documents are not being sent",
                sessionStatus is null
                    ? "The WhatsApp gateway could not be reached, so customer documents are waiting. Check OpenWA on 10.10.10.9."
                    : $"The documents session is '{sessionStatus}', not ready, so customer documents are waiting. Reconnect it from the WhatsApp console.",
                cancellationToken);

            return new DispatchCustomerDocumentDeliveriesResult(
                $"The documents session is {sessionStatus ?? "unreachable"}.", Reconciled: reconciled);
        }

        var pass = await OpenPassAsync(runtime, now, cancellationToken);

        var candidates = await LoadCandidatesAsync(now, pass.AutoRemaining > 0, command.SapHeldBack, pass, cancellationToken);
        if (candidates.Count == 0)
        {
            return new DispatchCustomerDocumentDeliveriesResult("Nothing was due.", Reconciled: reconciled);
        }

        if (pass.SendsRemaining <= 0)
        {
            await alerts.RaiseAsync(
                CapReachedAlert,
                "WhatsApp document cap reached",
                $"{candidates.Count} customer document(s) are waiting because the hourly or daily sending cap has been reached. They go out as the cap allows.",
                cancellationToken);

            return new DispatchCustomerDocumentDeliveriesResult("The sending cap has been reached.", Reconciled: reconciled);
        }

        var deadline = now.AddMinutes(Math.Max(1, settings.MaxPassMinutes));
        var outcome = "Every due document was handled.";

        foreach (var candidate in candidates)
        {
            if (pass.SendsRemaining <= 0)
            {
                outcome = "The sending cap for this pass was reached.";
                break;
            }

            if (pacer.UtcNow >= deadline)
            {
                outcome = "The pass ran out of time; the rest wait for the next.";
                break;
            }

            if (Stopping)
            {
                outcome = "The application is stopping.";
                break;
            }

            if (candidate.Trigger == CustomerDocumentDeliveryTrigger.Auto && pass.AutoRemaining <= 0)
            {
                continue;
            }

            var result = await ProcessAsync(candidate.Id, sessionId, pass, cancellationToken);
            if (result == RowOutcome.StopPass)
            {
                outcome = "The gateway refused this server; sending is paused.";
                break;
            }
        }

        await RaisePassAlertsAsync(pass, cancellationToken);

        return new DispatchCustomerDocumentDeliveriesResult(
            outcome,
            pass.Sent,
            pass.Deferred,
            pass.Closed,
            pass.NewUncertain,
            reconciled);
    }

    // ── Before sending ──────────────────────────────────────────────────

    /// <summary>
    /// Nothing is claimed without a session. When that is because the gateway has no number to offer,
    /// or several and no way to tell them apart, and documents are waiting on it, an administrator is
    /// told — that is the one case a person still has to act on.
    /// </summary>
    private async Task<ErrorOr<DispatchCustomerDocumentDeliveriesResult>> IdleWithoutSessionAsync(
        SendingSession sending,
        CancellationToken cancellationToken)
    {
        var reason = sending.Kind switch
        {
            SendingSessionKind.Stopped => "Sending was stopped by an administrator.",
            SendingSessionKind.NoneReady => "No WhatsApp number is connected to send documents from.",
            SendingSessionKind.SeveralReady => "Several WhatsApp numbers are connected and none is marked for documents.",
            _ => "The WhatsApp gateway could not be asked which number sends documents."
        };

        if (sending.Kind is SendingSessionKind.NoneReady or SendingSessionKind.SeveralReady)
        {
            var waiting = await context.CustomerDocumentDeliveries
                .AsNoTracking()
                .CountAsync(delivery => delivery.Status == CustomerDocumentDeliveryStatus.Pending
                    || delivery.Status == CustomerDocumentDeliveryStatus.WaitingForFiscal, cancellationToken);

            if (waiting > 0)
            {
                await alerts.RaiseAsync(
                    NoSessionAlert,
                    "WhatsApp documents have no number to go from",
                    sending.Kind == SendingSessionKind.NoneReady
                        ? $"{waiting} customer document(s) are waiting because no WhatsApp number is connected. Connect one on the WhatsApp Inbox; it is used as soon as it is ready."
                        : $"{waiting} customer document(s) are waiting because several WhatsApp numbers are connected and none is named '{options.Value.PreferredSessionName}'. Choose the one that sends documents on WhatsApp Deliveries.",
                    cancellationToken);
            }
        }

        return DispatchCustomerDocumentDeliveriesResult.Idle(reason);
    }

    /// <summary>
    /// Puts back what an earlier pass left half done: a claim that never got as far as sending is
    /// released; a send that never recorded its answer becomes uncertain, to be settled from the log.
    /// </summary>
    private async Task HousekeepAsync(DateTime now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var stalePreparing = now.AddMinutes(-Math.Max(1, settings.StalePreparingMinutes));
        var staleSending = now.AddSeconds(-(Math.Max(1, openWaOptions.Value.DocumentTimeoutSeconds) + 300));

        await context.CustomerDocumentDeliveries
            .Where(delivery => delivery.Status == CustomerDocumentDeliveryStatus.Preparing
                && delivery.ClaimedAtUtc != null
                && delivery.ClaimedAtUtc < stalePreparing)
            .ExecuteUpdateAsync(update => update
                .SetProperty(delivery => delivery.Status, CustomerDocumentDeliveryStatus.Pending)
                .SetProperty(delivery => delivery.ClaimToken, (Guid?)null)
                .SetProperty(delivery => delivery.UpdatedAtUtc, now),
                cancellationToken);

        var cutOff = await context.CustomerDocumentDeliveries
            .Where(delivery => delivery.Status == CustomerDocumentDeliveryStatus.Sending
                && delivery.SendIssuedAtUtc != null
                && delivery.SendIssuedAtUtc < staleSending)
            .ExecuteUpdateAsync(update => update
                .SetProperty(delivery => delivery.Status, CustomerDocumentDeliveryStatus.Uncertain)
                .SetProperty(delivery => delivery.StatusReason,
                    "The send was cut off before the gateway answered; its log will show whether it went.")
                .SetProperty(delivery => delivery.ClaimToken, (Guid?)null)
                .SetProperty(delivery => delivery.UpdatedAtUtc, now),
                cancellationToken);

        if (cutOff > 0)
        {
            logger.LogWarning("{Count} customer document send(s) were cut off before the gateway answered", cutOff);
        }
    }

    /// <summary>Settles up to ten uncertain sends from the gateway's own message log.</summary>
    private async Task<int> ReconcileUncertainAsync(string sessionId, DateTime now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var settleBefore = now.AddMinutes(-Math.Max(1, settings.UncertainReconcileAfterMinutes));
        var giveUpBefore = now - ReconcileWindow;
        var documentTimeout = TimeSpan.FromSeconds(Math.Max(1, openWaOptions.Value.DocumentTimeoutSeconds));

        context.ChangeTracker.Clear();
        var rows = await context.CustomerDocumentDeliveries
            .AsTracking()
            .Where(delivery => delivery.Status == CustomerDocumentDeliveryStatus.Uncertain
                && delivery.SendIssuedAtUtc != null
                && delivery.SendIssuedAtUtc <= settleBefore
                && delivery.SendIssuedAtUtc >= giveUpBefore)
            .OrderBy(delivery => delivery.SendIssuedAtUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        var settled = 0;
        foreach (var row in rows)
        {
            WhatsAppMessageHistoryDto history;
            try
            {
                history = await openWaClient.GetMessagesAsync(
                    row.SessionId ?? sessionId,
                    WhatsAppRecipients.ChatId(row.RecipientE164),
                    50,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read the gateway's log to settle delivery {DeliveryId}", row.Id);
                continue;
            }

            var decision = UncertainDeliveryReconciler.Decide(row, history.Messages, documentTimeout);
            row.StatusReason = decision.Reason;
            row.UpdatedAtUtc = now;

            switch (decision.Status)
            {
                case CustomerDocumentDeliveryStatus.Sent:
                case CustomerDocumentDeliveryStatus.SentUnconfirmed:
                    row.Status = decision.Status;
                    row.MessageId ??= CustomerDocumentDeliveryRules.Truncate(decision.MessageId, 200);
                    row.SentAtUtc ??= row.SendIssuedAtUtc;
                    row.ClosedAtUtc = now;
                    break;
                case CustomerDocumentDeliveryStatus.Failed:
                    row.Status = CustomerDocumentDeliveryStatus.Failed;
                    row.ClosedAtUtc = now;
                    break;
                default:
                    row.Status = CustomerDocumentDeliveryStatus.Pending;
                    row.SendIssuedAtUtc = null;
                    row.NextAttemptAtUtc = now;
                    break;
            }

            settled++;
        }

        if (settled > 0)
        {
            await context.SaveChangesAsync(CancellationToken.None);
        }

        context.ChangeTracker.Clear();
        return settled;
    }

    /// <summary>The documents session's state, or null when the gateway could not be asked.</summary>
    private async Task<string?> ReadSessionStatusAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var sessions = await openWaClient.GetSessionsAsync(cancellationToken);
            var session = sessions.FirstOrDefault(row => string.Equals(row.Id, sessionId, StringComparison.OrdinalIgnoreCase));
            return session?.Status ?? "missing";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the WhatsApp sessions before sending customer documents");
            return null;
        }
    }

    /// <summary>Counts today's sends against the caps, cluster-wide, from when each send was issued.</summary>
    private async Task<PassState> OpenPassAsync(
        CustomerDocumentRuntimeSettings runtime,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var dayStart = DeliveryBudget.CatDayStartUtc(now);
        var hourAgo = now.AddHours(-1);

        var issued = context.CustomerDocumentDeliveries
            .AsNoTracking()
            .Where(delivery => delivery.SendIssuedAtUtc != null);

        var sentToday = await issued.CountAsync(delivery => delivery.SendIssuedAtUtc >= dayStart, cancellationToken);
        var sentLastHour = await issued.CountAsync(delivery => delivery.SendIssuedAtUtc >= hourAgo, cancellationToken);
        var autoSentToday = await issued.CountAsync(
            delivery => delivery.SendIssuedAtUtc >= dayStart && delivery.Trigger == CustomerDocumentDeliveryTrigger.Auto,
            cancellationToken);
        var lastIssued = await issued.MaxAsync(delivery => delivery.SendIssuedAtUtc, cancellationToken);
        var checksToday = await WhatsAppGateway.NumberChecksSinceAsync(context, dayStart, cancellationToken);

        var sendsRemaining = Math.Min(
            Math.Max(0, settings.MaxSendsPerPass),
            Math.Min(settings.HardMaxPerDay - sentToday, settings.MaxPerHour - sentLastHour));

        var autoRemaining = runtime.AutoSendEnabled && DeliveryBudget.IsWithinAutoWindow(now, settings)
            ? runtime.MaxAutoPerDay - autoSentToday
            : 0;

        return new PassState
        {
            SendsRemaining = Math.Max(0, sendsRemaining),
            AutoRemaining = Math.Max(0, autoRemaining),
            ChecksRemaining = Math.Max(0, settings.MaxNumberChecksPerDay - checksToday),
            LastIssuedUtc = lastIssued
        };
    }

    private async Task<List<Candidate>> LoadCandidatesAsync(
        DateTime now,
        bool autoAllowed,
        bool sapHeldBack,
        PassState pass,
        CancellationToken cancellationToken)
    {
        var due = context.CustomerDocumentDeliveries
            .AsNoTracking()
            .Where(delivery => (delivery.Status == CustomerDocumentDeliveryStatus.Pending
                    || delivery.Status == CustomerDocumentDeliveryStatus.WaitingForFiscal)
                && delivery.NextAttemptAtUtc <= now);

        if (!autoAllowed)
            due = due.Where(delivery => delivery.Trigger != CustomerDocumentDeliveryTrigger.Auto);

        if (sapHeldBack)
            due = due.Where(delivery => delivery.DocumentType != CustomerDocumentType.SapInvoice);

        // More than can be sent, because a good share only learn they are still waiting for a receipt.
        var take = Math.Max(20, pass.SendsRemaining * 5);

        return await due
            .OrderByDescending(delivery => delivery.Priority)
            .ThenBy(delivery => delivery.CreatedAtUtc)
            .Select(delivery => new Candidate(delivery.Id, delivery.Trigger))
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    // ── One document ────────────────────────────────────────────────────

    private async Task<RowOutcome> ProcessAsync(long id, string sessionId, PassState pass, CancellationToken cancellationToken)
    {
        var now = pacer.UtcNow;
        var token = Guid.NewGuid();

        var claimed = await context.CustomerDocumentDeliveries
            .Where(delivery => delivery.Id == id
                && (delivery.Status == CustomerDocumentDeliveryStatus.Pending
                    || delivery.Status == CustomerDocumentDeliveryStatus.WaitingForFiscal)
                && delivery.NextAttemptAtUtc <= now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(delivery => delivery.Status, CustomerDocumentDeliveryStatus.Preparing)
                .SetProperty(delivery => delivery.ClaimToken, token)
                .SetProperty(delivery => delivery.ClaimedAtUtc, now)
                .SetProperty(delivery => delivery.UpdatedAtUtc, now),
                cancellationToken);

        if (claimed == 0)
            return RowOutcome.Skipped;

        // ExecuteUpdate never touches the change tracker; a copy tracked earlier would be stale.
        context.ChangeTracker.Clear();
        var row = await context.CustomerDocumentDeliveries
            .AsTracking()
            .SingleAsync(delivery => delivery.Id == id, cancellationToken);

        if (row.ClaimToken != token)
            return RowOutcome.Skipped;

        try
        {
            return await PrepareAndSendAsync(row, sessionId, pass, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (row.Status == CustomerDocumentDeliveryStatus.Preparing)
            {
                await DeferAsync(row, pass, CustomerDocumentDeliveryStatus.Pending, pacer.UtcNow,
                    "Put back: the application stopped before it was sent.");
            }

            throw;
        }
        catch (Exception ex) when (row.Status == CustomerDocumentDeliveryStatus.Preparing)
        {
            logger.LogError(ex, "Could not prepare customer document delivery {DeliveryId}", row.Id);
            return await DeferAsync(row, pass, CustomerDocumentDeliveryStatus.Pending, pacer.UtcNow.Add(PrepareRetryDelay),
                $"Could not prepare the document: {ex.Message}");
        }
    }

    private async Task<RowOutcome> PrepareAndSendAsync(
        CustomerDocumentDeliveryEntity row,
        string sessionId,
        PassState pass,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = pacer.UtcNow;

        var contact = row.ContactId is { } contactId
            ? await context.CustomerWhatsAppContacts.AsTracking().FirstOrDefaultAsync(c => c.Id == contactId, cancellationToken)
            : null;

        var consentProblem = await FindConsentProblemAsync(row, contact, cancellationToken);
        if (consentProblem is not null)
            return await CloseAsync(row, pass, CustomerDocumentDeliveryStatus.Cancelled, consentProblem);

        var dayStart = DeliveryBudget.CatDayStartUtc(now);
        var toRecipientToday = await context.CustomerDocumentDeliveries
            .AsNoTracking()
            .CountAsync(delivery => delivery.RecipientE164 == row.RecipientE164
                && delivery.SendIssuedAtUtc != null
                && delivery.SendIssuedAtUtc >= dayStart, cancellationToken);

        if (toRecipientToday >= settings.MaxPerRecipientPerDay)
        {
            return await DeferAsync(row, pass, CustomerDocumentDeliveryStatus.Pending, DeliveryBudget.NextCatDayStartUtc(now),
                $"This number has had {toRecipientToday} documents today, the most allowed; it goes tomorrow.");
        }

        var composition = row.DocumentType == CustomerDocumentType.SapInvoice
            ? await sapInvoiceComposer.ComposeAsync(row, now, cancellationToken)
            : DocumentComposition.Fail("Till receipts cannot be sent on WhatsApp yet.");

        var age = now - row.CreatedAtUtc;
        var waitedTooLong = age > TimeSpan.FromHours(Math.Max(1, settings.MaxFiscalWaitHours));

        switch (composition.Kind)
        {
            case DocumentCompositionKind.WaitForFiscal when waitedTooLong:
            case DocumentCompositionKind.Retry when waitedTooLong:
                pass.NewHeld++;
                return await HoldAsync(row, pass,
                    $"Not sent after {settings.MaxFiscalWaitHours} hours: {composition.Reason}");
            case DocumentCompositionKind.WaitForFiscal:
                return await DeferAsync(row, pass, CustomerDocumentDeliveryStatus.WaitingForFiscal,
                    now.Add(DeliveryBudget.FiscalRecheckDelay(age)), composition.Reason);
            case DocumentCompositionKind.Retry:
                return await DeferAsync(row, pass, CustomerDocumentDeliveryStatus.Pending,
                    now.Add(PrepareRetryDelay), composition.Reason);
            case DocumentCompositionKind.Hold:
                pass.NewHeld++;
                return await HoldAsync(row, pass, composition.Reason);
            case DocumentCompositionKind.Cancel:
                return await CloseAsync(row, pass, CustomerDocumentDeliveryStatus.Cancelled, composition.Reason);
            case DocumentCompositionKind.Fail:
                return await CloseAsync(row, pass, CustomerDocumentDeliveryStatus.Failed, composition.Reason);
        }

        var document = composition.Document!;

        var exists = await CheckRecipientAsync(row, contact, sessionId, now, pass, cancellationToken);
        if (exists == false)
        {
            return await CloseAsync(row, pass, CustomerDocumentDeliveryStatus.NotOnWhatsApp,
                "WhatsApp has no account for this number.");
        }

        if (pass.LastIssuedUtc is { } lastIssued)
        {
            var gap = DeliveryBudget.Gap(settings, pacer.JitterSeconds(settings.JitterSeconds));
            var wait = lastIssued.Add(gap) - pacer.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await pacer.DelayAsync(wait, cancellationToken);
            }
        }

        if (Stopping)
        {
            await DeferAsync(row, pass, CustomerDocumentDeliveryStatus.Pending, pacer.UtcNow,
                "Put back: the application was stopping.");
            return RowOutcome.StopPass;
        }

        var issued = pacer.UtcNow;
        row.Status = CustomerDocumentDeliveryStatus.Sending;
        row.SendIssuedAtUtc = issued;
        row.SessionId = sessionId;
        row.FileName = document.FileName;
        row.FileSha256 = document.Sha256;
        row.FileBytes = document.Bytes.Length;
        row.Caption = document.Caption;
        row.FiscalQrCode = CustomerDocumentDeliveryRules.Truncate(document.Receipt.QrCode, 500);
        row.FiscalVerificationCode = CustomerDocumentDeliveryRules.Truncate(document.Receipt.VerificationCode, 200);
        row.FiscalEvidenceSource = document.FiscalEvidenceSource;
        row.StatusReason = null;
        row.LastError = null;
        row.UpdatedAtUtc = issued;

        // The commit point. From here the send is an obligation: nothing below takes the job's token.
        await context.SaveChangesAsync(CancellationToken.None);
        pass.LastIssuedUtc = issued;
        pass.SendsRemaining--;
        if (row.Trigger == CustomerDocumentDeliveryTrigger.Auto)
            pass.AutoRemaining--;

        OpenWADispatchOutcome outcome;
        try
        {
            var result = await openWaClient.SendDocumentAsync(
                sessionId,
                new WhatsAppSendDocumentRequestDto
                {
                    ChatId = WhatsAppRecipients.ChatId(row.RecipientE164),
                    Base64 = Convert.ToBase64String(document.Bytes),
                    Mimetype = "application/pdf",
                    Filename = document.FileName,
                    Caption = document.Caption
                },
                CancellationToken.None);

            outcome = OpenWADispatchOutcomeClassifier.Classify(result);
        }
        catch (Exception ex)
        {
            outcome = OpenWADispatchOutcomeClassifier.Classify(ex);
        }

        return await ApplyOutcomeAsync(row, contact, outcome, pass);
    }

    /// <summary>Why this document may no longer go to this number, or null when it may.</summary>
    private async Task<string?> FindConsentProblemAsync(
        CustomerDocumentDeliveryEntity row,
        CustomerWhatsAppContactEntity? contact,
        CancellationToken cancellationToken)
    {
        if (row.ContactId is not null)
        {
            if (contact is null || contact.RemovedAtUtc is not null)
                return "The number was taken off the customer before it went.";

            if (contact.OptedOutAtUtc is not null)
                return "The customer asked not to receive documents on this number before it went.";

            if (row.Trigger == CustomerDocumentDeliveryTrigger.Auto && !contact.AutoSendInvoices)
                return "Automatic sending was turned off for this number before it went.";

            return null;
        }

        var optedOut = await context.CustomerWhatsAppContacts
            .AsNoTracking()
            .AnyAsync(saved => saved.PhoneE164 == row.RecipientE164
                && saved.OptedOutAtUtc != null
                && saved.RemovedAtUtc == null, cancellationToken);

        return optedOut ? "The customer asked not to receive documents on this number before it went." : null;
    }

    /// <summary>
    /// Whether the number has a WhatsApp account: true, false, or null when it was not asked — the
    /// daily cap is spent, or the gateway could not answer — in which case the document is sent anyway
    /// and the send itself is the test.
    /// </summary>
    private async Task<bool?> CheckRecipientAsync(
        CustomerDocumentDeliveryEntity row,
        CustomerWhatsAppContactEntity? contact,
        string sessionId,
        DateTime now,
        PassState pass,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (contact is not null)
        {
            if (contact.WhatsAppExists == true)
                return true;

            if (contact.WhatsAppExists == false
                && contact.WhatsAppCheckedAtUtc is { } checkedAt
                && now - checkedAt < TimeSpan.FromDays(Math.Max(1, settings.ContactRecheckDays)))
            {
                return false;
            }
        }
        else if (row.RecipientCheckedAtUtc is not null)
        {
            return null;
        }

        if (pass.ChecksRemaining <= 0)
            return null;

        try
        {
            var check = await openWaClient.CheckNumberAsync(sessionId, WhatsAppRecipients.Digits(row.RecipientE164), cancellationToken);
            pass.ChecksRemaining--;

            if (contact is not null)
            {
                contact.WhatsAppExists = check.Exists;
                contact.WhatsAppCheckedAtUtc = now;
                contact.UpdatedAtUtc = now;
            }
            else
            {
                row.RecipientCheckedAtUtc = now;
            }

            return check.Exists;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check {Recipient} against WhatsApp before delivery {DeliveryId}",
                WhatsAppRecipients.Mask(row.RecipientE164), row.Id);
            return null;
        }
    }

    // ── After sending ───────────────────────────────────────────────────

    private async Task<RowOutcome> ApplyOutcomeAsync(
        CustomerDocumentDeliveryEntity row,
        CustomerWhatsAppContactEntity? contact,
        OpenWADispatchOutcome outcome,
        PassState pass)
    {
        var settings = options.Value;
        var now = pacer.UtcNow;
        var result = RowOutcome.Sent;

        row.ClaimToken = null;
        row.UpdatedAtUtc = now;
        row.LastError = CustomerDocumentDeliveryRules.Truncate(outcome.Error, 1000);

        switch (outcome.Kind)
        {
            case OpenWADispatchOutcomeKind.Sent:
                row.Status = CustomerDocumentDeliveryStatus.Sent;
                row.MessageId = CustomerDocumentDeliveryRules.Truncate(outcome.MessageId, 200);
                row.GatewayTimestamp = outcome.GatewayTimestamp;
                row.SentAtUtc = now;
                row.ClosedAtUtc = now;
                if (contact is not null)
                {
                    // A delivered message is a better answer than any check, and it costs no check.
                    contact.WhatsAppExists = true;
                }

                pass.Sent++;
                break;

            case OpenWADispatchOutcomeKind.SentUnconfirmed:
                row.Status = CustomerDocumentDeliveryStatus.SentUnconfirmed;
                row.GatewayTimestamp = outcome.GatewayTimestamp;
                row.SentAtUtc = now;
                row.ClosedAtUtc = now;
                row.StatusReason = "WhatsApp accepted it without a message id to confirm it by; it has very likely arrived.";
                pass.Sent++;
                break;

            case OpenWADispatchOutcomeKind.NotSent:
                row.DispatchAttempts++;
                row.SendIssuedAtUtc = null;
                pass.SendsRemaining++;
                if (row.Trigger == CustomerDocumentDeliveryTrigger.Auto)
                    pass.AutoRemaining++;

                if (row.DispatchAttempts >= Math.Max(1, settings.MaxDispatchAttempts))
                {
                    row.Status = CustomerDocumentDeliveryStatus.Failed;
                    row.ClosedAtUtc = now;
                    row.StatusReason = $"Not sent after {row.DispatchAttempts} attempts.";
                    pass.Closed++;
                }
                else
                {
                    var next = now.Add(DeliveryBudget.DispatchBackoff(row.DispatchAttempts));
                    row.Status = CustomerDocumentDeliveryStatus.Pending;
                    row.NextAttemptAtUtc = next;
                    row.StatusReason = $"Not sent; trying again at {AuditService.ToCAT(next):HH:mm} CAT.";
                    pass.Deferred++;
                }

                result = RowOutcome.Deferred;
                break;

            case OpenWADispatchOutcomeKind.Rejected:
                row.Status = CustomerDocumentDeliveryStatus.Failed;
                row.ClosedAtUtc = now;
                row.SendIssuedAtUtc = null;
                row.StatusReason = "The gateway refused the document.";
                pass.Closed++;
                result = RowOutcome.Closed;
                break;

            case OpenWADispatchOutcomeKind.Paused:
                row.Status = CustomerDocumentDeliveryStatus.Pending;
                row.SendIssuedAtUtc = null;
                row.NextAttemptAtUtc = now.Add(PausedRetryDelay);
                row.StatusReason = "The gateway refused this server; sending is paused until it is fixed.";
                pass.Deferred++;
                result = RowOutcome.StopPass;
                break;

            default:
                row.Status = CustomerDocumentDeliveryStatus.Uncertain;
                row.StatusReason = "The answer from WhatsApp was lost; the gateway's log will show whether it went.";
                pass.NewUncertain++;
                result = RowOutcome.Uncertain;
                break;
        }

        await context.SaveChangesAsync(CancellationToken.None);

        if (outcome.AlertCondition is { } condition)
        {
            await alerts.RaiseAsync(
                condition,
                AlertTitle(condition),
                $"Sending invoice {row.DocumentNumber}: {outcome.Error}",
                CancellationToken.None);
        }

        return result;
    }

    private async Task<RowOutcome> DeferAsync(
        CustomerDocumentDeliveryEntity row,
        PassState pass,
        CustomerDocumentDeliveryStatus status,
        DateTime nextAttemptUtc,
        string? reason)
    {
        row.Status = status;
        row.NextAttemptAtUtc = nextAttemptUtc;
        row.StatusReason = CustomerDocumentDeliveryRules.Truncate(reason, 500);
        row.ClaimToken = null;
        row.UpdatedAtUtc = pacer.UtcNow;
        await context.SaveChangesAsync(CancellationToken.None);
        pass.Deferred++;
        return RowOutcome.Deferred;
    }

    private async Task<RowOutcome> HoldAsync(CustomerDocumentDeliveryEntity row, PassState pass, string? reason)
    {
        row.Status = CustomerDocumentDeliveryStatus.Held;
        row.StatusReason = CustomerDocumentDeliveryRules.Truncate(reason, 500);
        row.ClaimToken = null;
        row.UpdatedAtUtc = pacer.UtcNow;
        await context.SaveChangesAsync(CancellationToken.None);
        pass.Closed++;
        return RowOutcome.Closed;
    }

    private async Task<RowOutcome> CloseAsync(
        CustomerDocumentDeliveryEntity row,
        PassState pass,
        CustomerDocumentDeliveryStatus status,
        string? reason)
    {
        var now = pacer.UtcNow;
        row.Status = status;
        row.StatusReason = CustomerDocumentDeliveryRules.Truncate(reason, 500);
        row.ClaimToken = null;
        row.ClosedAtUtc = now;
        row.UpdatedAtUtc = now;
        await context.SaveChangesAsync(CancellationToken.None);
        pass.Closed++;
        return RowOutcome.Closed;
    }

    private async Task RaisePassAlertsAsync(PassState pass, CancellationToken cancellationToken)
    {
        if (pass.NewHeld > 0)
        {
            await alerts.RaiseAsync(
                HeldAlert,
                "WhatsApp documents need a look",
                $"{pass.NewHeld} customer document(s) were held: their fiscal receipt could not be confirmed. See WhatsApp Deliveries.",
                cancellationToken);
        }

        if (pass.NewUncertain > 0)
        {
            await alerts.RaiseAsync(
                UncertainAlert,
                "WhatsApp document sends unconfirmed",
                $"{pass.NewUncertain} send(s) lost their answer from WhatsApp. They are being checked against the gateway's log.",
                cancellationToken);
        }

        var backlog = await context.CustomerDocumentDeliveries
            .AsNoTracking()
            .CountAsync(delivery => delivery.Status == CustomerDocumentDeliveryStatus.Pending
                || delivery.Status == CustomerDocumentDeliveryStatus.WaitingForFiscal, cancellationToken);

        if (backlog > options.Value.BacklogAlertThreshold)
        {
            await alerts.RaiseAsync(
                BacklogAlert,
                "WhatsApp documents are backing up",
                $"{backlog} customer documents are waiting to be sent.",
                cancellationToken);
        }
    }

    private static string AlertTitle(string condition) => condition switch
    {
        OpenWADispatchOutcomeClassifier.SessionDownAlert => "WhatsApp documents session is down",
        OpenWADispatchOutcomeClassifier.DocumentTooLargeAlert => "WhatsApp gateway refused a document as too large",
        _ => "WhatsApp gateway refused a document"
    };

    private void WarnUnconfigured()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastUnconfiguredWarningTicks);
        if (now - last < TimeSpan.FromMinutes(30).Ticks)
            return;

        Interlocked.Exchange(ref _lastUnconfiguredWarningTicks, now);
        logger.LogWarning(
            "Customer documents are enabled but OpenWA is not configured on this node, so it sends none. "
            + "Another node with OpenWA configured sends them.");
    }

    private sealed record Candidate(long Id, CustomerDocumentDeliveryTrigger Trigger);

    private enum RowOutcome
    {
        Skipped,
        Sent,
        Deferred,
        Closed,
        Uncertain,
        StopPass
    }

    /// <summary>What one pass has left to spend, and what it has done.</summary>
    private sealed class PassState
    {
        public int SendsRemaining { get; set; }
        public int AutoRemaining { get; set; }
        public int ChecksRemaining { get; set; }
        public DateTime? LastIssuedUtc { get; set; }
        public int Sent { get; set; }
        public int Deferred { get; set; }
        public int Closed { get; set; }
        public int NewHeld { get; set; }
        public int NewUncertain { get; set; }
    }
}
