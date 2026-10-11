using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>Where the question "which number sends this?" stands.</summary>
internal enum SendingSessionKind
{
    /// <summary>A session is chosen: saved by an administrator, or picked just now.</summary>
    Chosen = 0,

    /// <summary>An administrator stopped all sending on WhatsApp Deliveries.</summary>
    Stopped = 1,

    /// <summary>
    /// None is saved and this node could not ask the gateway — it has no gateway settings, or the
    /// gateway did not answer. Another node, or a later pass, may still choose one.
    /// </summary>
    CannotAsk = 2,

    /// <summary>The gateway answered and has no session that is ready to send.</summary>
    NoneReady = 3,

    /// <summary>The gateway has several ready sessions and nothing says which one sends documents.</summary>
    SeveralReady = 4
}

/// <param name="Kind">How the question was answered.</param>
/// <param name="SessionId">The session, when <see cref="SendingSessionKind.Chosen"/>.</param>
/// <param name="PickedNow">True when this call chose the session and saved it.</param>
internal sealed record SendingSession(SendingSessionKind Kind, string? SessionId = null, bool PickedNow = false)
{
    /// <summary>
    /// Why a person asking for a send must be refused, or null when the request may be queued. A node
    /// that could not ask the gateway queues: the node that sends chooses the session when it runs.
    /// </summary>
    public Error? Refusal(string preferredName) => Kind switch
    {
        SendingSessionKind.Stopped => Errors.CustomerDocuments.SendingStopped,
        SendingSessionKind.NoneReady => Errors.CustomerDocuments.NoSendingNumber,
        SendingSessionKind.SeveralReady => Errors.CustomerDocuments.SeveralSendingNumbers(preferredName),
        _ => null
    };
}

/// <summary>
/// The session customer documents are sent from, chosen without anyone having to set it up.
/// </summary>
/// <remarks>
/// <para>
/// A send used to be refused until an administrator had opened WhatsApp Deliveries and picked a
/// session, which nobody sending an invoice knew to ask for. Now, when none is saved, the gateway is
/// asked what it has: the ready session carrying the documents name
/// (<see cref="CustomerDocumentDeliverySettings.PreferredSessionName"/>) if there is one, otherwise the
/// only ready session. The pick is saved to <c>SystemConfigs</c> like an administrator's, so every
/// node sends from the same number from then on and the page shows — and can change — what was picked.
/// </para>
/// <para>
/// Two things are never guessed. Several ready sessions with none carrying the documents name are
/// left for a person: sending customers' invoices from the wrong number cannot be taken back. And
/// sending an administrator stopped stays stopped — choosing a session over that would turn it back
/// on behind their back.
/// </para>
/// </remarks>
internal static class CustomerDocumentSession
{
    /// <summary>Recorded as who chose the session when nobody did.</summary>
    public const string AutomaticActor = "Chosen automatically";

    private const string ReadyStatus = "ready";

    /// <summary>
    /// The saved session, or one picked from the gateway and saved. Call it before staging anything
    /// else on <paramref name="context"/>: a pick is saved here, on its own.
    /// </summary>
    public static async Task<SendingSession> EnsureAsync(
        ApplicationDbContext context,
        IOpenWAClient openWaClient,
        OpenWASettings openWaSettings,
        CustomerDocumentDeliverySettings settings,
        CustomerDocumentRuntimeSettings runtime,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (runtime.WhatsAppSessionId is { } saved)
            return new SendingSession(SendingSessionKind.Chosen, saved);

        if (runtime.SendingStopped)
            return new SendingSession(SendingSessionKind.Stopped);

        if (!WhatsAppGateway.IsConfigured(openWaSettings))
            return new SendingSession(SendingSessionKind.CannotAsk);

        List<WhatsAppSessionDto> sessions;
        try
        {
            sessions = await openWaClient.GetSessionsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the WhatsApp sessions to choose one for customer documents");
            return new SendingSession(SendingSessionKind.CannotAsk);
        }

        var pick = Pick(sessions, settings.PreferredSessionName);
        if (pick.Session is not { } session)
            return new SendingSession(pick.Kind);

        await CustomerDocumentDeliveryKeys.StageAsync(context, CustomerDocumentDeliveryKeys.WhatsAppSessionId, "string",
            session.Id,
            "The OpenWA session customer documents are sent from. Blank leaves it to be chosen automatically.",
            isEditable: true, cancellationToken);

        await CustomerDocumentDeliveryKeys.StageAsync(context, CustomerDocumentDeliveryKeys.SettingsChangedBy, "string",
            AutomaticActor,
            "Who last changed the customer document settings.",
            isEditable: false, cancellationToken);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Another node, or a request beside this one, saved the settings at the same moment. Theirs
            // stands: two nodes must never send from two numbers.
            logger.LogInformation(ex, "The customer documents session was saved by someone else while one was being chosen");
            context.ChangeTracker.Clear();

            var latest = await CustomerDocumentDeliveryKeys.ReadAsync(context, cancellationToken);
            return latest.WhatsAppSessionId is { } theirs
                ? new SendingSession(SendingSessionKind.Chosen, theirs)
                : new SendingSession(latest.SendingStopped ? SendingSessionKind.Stopped : SendingSessionKind.CannotAsk);
        }

        logger.LogInformation(
            "Customer documents are now sent from WhatsApp session {SessionName} ({SessionId}), chosen because none had been set",
            session.Name, session.Id);

        return new SendingSession(SendingSessionKind.Chosen, session.Id, PickedNow: true);
    }

    /// <summary>
    /// The session to send from among what the gateway lists, or why none can be chosen. Kept free of
    /// the database and the gateway so every case is a plain test.
    /// </summary>
    public static (WhatsAppSessionDto? Session, SendingSessionKind Kind) Pick(
        IReadOnlyCollection<WhatsAppSessionDto> sessions,
        string? preferredName)
    {
        var ready = sessions
            .Where(session => !string.IsNullOrWhiteSpace(session.Id)
                && string.Equals(session.Status, ReadyStatus, StringComparison.OrdinalIgnoreCase))
            .OrderBy(session => session.Id, StringComparer.Ordinal)
            .ToList();

        if (ready.Count == 0)
            return (null, SendingSessionKind.NoneReady);

        var named = string.IsNullOrWhiteSpace(preferredName)
            ? []
            : ready.Where(session => string.Equals(session.Name?.Trim(), preferredName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        if (named.Count == 1)
            return (named[0], SendingSessionKind.Chosen);

        return ready.Count == 1
            ? (ready[0], SendingSessionKind.Chosen)
            : (null, SendingSessionKind.SeveralReady);
    }
}
