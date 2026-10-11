using Microsoft.EntityFrameworkCore;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>
/// Where sending customer documents stands — read for the WhatsApp Deliveries page, and handed back
/// after its settings are saved so the page shows what it just changed.
/// </summary>
internal static class CustomerDocumentStatusReader
{
    public static async Task<CustomerDocumentDeliveryStatusDto> ReadAsync(
        ApplicationDbContext context,
        IOpenWAClient openWaClient,
        OpenWASettings openWaSettings,
        CustomerDocumentDeliverySettings settings,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var runtime = await CustomerDocumentDeliveryKeys.ReadAsync(context, cancellationToken);
        var now = DateTime.UtcNow;
        var dayStart = DeliveryBudget.CatDayStartUtc(now);
        var hourAgo = now.AddHours(-1);
        var deliveries = context.CustomerDocumentDeliveries.AsNoTracking();

        var status = new CustomerDocumentDeliveryStatusDto
        {
            Enabled = settings.Enabled,
            GatewayConfigured = WhatsAppGateway.IsConfigured(openWaSettings),
            AutoSendEnabled = runtime.AutoSendEnabled,
            SessionId = runtime.WhatsAppSessionId,
            SendingStopped = runtime.SendingStopped,
            MaxAutoPerDay = runtime.MaxAutoPerDay,
            MaxPerHour = settings.MaxPerHour,
            HardMaxPerDay = settings.HardMaxPerDay,
            AutoWindow = $"{settings.AutoWindowStartCat}–{settings.AutoWindowEndCat} CAT"
                + (settings.AutoSendOnSundays ? string.Empty : ", not on Sundays"),
            WithinAutoWindow = DeliveryBudget.IsWithinAutoWindow(now, settings),
            SettingsChangedBy = runtime.ChangedBy,
            SettingsChangedAtUtc = runtime.ChangedAtUtc,
            SentToday = await deliveries.CountAsync(d => d.SendIssuedAtUtc != null && d.SendIssuedAtUtc >= dayStart, cancellationToken),
            SentLastHour = await deliveries.CountAsync(d => d.SendIssuedAtUtc != null && d.SendIssuedAtUtc >= hourAgo, cancellationToken),
            AutoSentToday = await deliveries.CountAsync(
                d => d.SendIssuedAtUtc != null && d.SendIssuedAtUtc >= dayStart && d.Trigger == CustomerDocumentDeliveryTrigger.Auto,
                cancellationToken),
            Waiting = await deliveries.CountAsync(
                d => d.Status == CustomerDocumentDeliveryStatus.Pending || d.Status == CustomerDocumentDeliveryStatus.Preparing,
                cancellationToken),
            WaitingForFiscal = await deliveries.CountAsync(d => d.Status == CustomerDocumentDeliveryStatus.WaitingForFiscal, cancellationToken),
            Held = await deliveries.CountAsync(d => d.Status == CustomerDocumentDeliveryStatus.Held, cancellationToken),
            Uncertain = await deliveries.CountAsync(d => d.Status == CustomerDocumentDeliveryStatus.Uncertain, cancellationToken),
            FailedToday = await deliveries.CountAsync(
                d => d.Status == CustomerDocumentDeliveryStatus.Failed && d.ClosedAtUtc != null && d.ClosedAtUtc >= dayStart,
                cancellationToken),
            LastSentAtUtc = await deliveries.MaxAsync(d => d.SentAtUtc, cancellationToken)
        };

        if (await InvoiceScanCheckpoint.ReadAsync(context, cancellationToken) is { } scan)
        {
            status.InvoiceScanAtUtc = scan.LastScanAtUtc;
            status.InvoiceScanLastDocEntry = scan.LastDocEntry;
        }

        if (!status.GatewayConfigured)
        {
            status.SessionError = "WhatsApp is not configured on the server that answered.";
            return status;
        }

        try
        {
            var sessions = await openWaClient.GetSessionsAsync(cancellationToken);
            status.Sessions = sessions
                .Select(session => new WhatsAppSessionOptionDto
                {
                    Id = session.Id,
                    Name = session.Name,
                    Status = session.Status,
                    Phone = session.Phone
                })
                .OrderBy(session => session.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var selected = status.Sessions.FirstOrDefault(session =>
                string.Equals(session.Id, runtime.WhatsAppSessionId, StringComparison.OrdinalIgnoreCase));

            status.SessionName = selected?.Name;
            status.SessionStatus = selected?.Status;
            status.SessionPhone = selected?.Phone;

            if (runtime.WhatsAppSessionId is not null && selected is null)
            {
                status.SessionError = "The session chosen to send documents no longer exists on the gateway.";
            }

            // Nothing is chosen here — a read does not write. This only says what the next pass will do.
            if (runtime.WhatsAppSessionId is null && !runtime.SendingStopped)
            {
                var pick = CustomerDocumentSession.Pick(sessions, settings.PreferredSessionName);
                status.AutomaticSessionName = pick.Session?.Name;
                status.SeveralSessionsReady = pick.Kind == SendingSessionKind.SeveralReady;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the WhatsApp sessions for the customer documents status");
            status.SessionError = "The WhatsApp gateway could not be reached.";
        }

        return status;
    }
}
