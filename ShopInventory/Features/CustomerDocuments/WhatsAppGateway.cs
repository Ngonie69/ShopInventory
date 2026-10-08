using Microsoft.EntityFrameworkCore;
using ShopInventory.Configuration;
using ShopInventory.Data;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>
/// Whether this node can talk to the gateway, and how many WhatsApp number checks today has left.
/// </summary>
internal static class WhatsAppGateway
{
    /// <summary>
    /// Enabled, with an address and a key that are not unfilled placeholders — the same test the
    /// WhatsApp console applies. Each node answers for itself: the gateway's settings live in each
    /// node's own web.config, and a node without them must leave the sending to one that has them.
    /// </summary>
    public static bool IsConfigured(OpenWASettings settings) =>
        settings.Enabled
        && !string.IsNullOrWhiteSpace(settings.BaseUrl)
        && !settings.BaseUrl.StartsWith("${", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(settings.ApiKey)
        && !settings.ApiKey.StartsWith("${", StringComparison.Ordinal);

    /// <summary>
    /// The number checks made since <paramref name="dayStartUtc"/>: every contact checked, and every
    /// one-off number checked for a send. Asking WhatsApp about many numbers is itself something it
    /// watches for, so the checks share a daily cap.
    /// </summary>
    public static async Task<int> NumberChecksSinceAsync(
        ApplicationDbContext context,
        DateTime dayStartUtc,
        CancellationToken cancellationToken)
    {
        var contacts = await context.CustomerWhatsAppContacts
            .AsNoTracking()
            .CountAsync(contact => contact.WhatsAppCheckedAtUtc != null && contact.WhatsAppCheckedAtUtc >= dayStartUtc, cancellationToken);

        var oneOffs = await context.CustomerDocumentDeliveries
            .AsNoTracking()
            .CountAsync(delivery => delivery.RecipientCheckedAtUtc != null && delivery.RecipientCheckedAtUtc >= dayStartUtc, cancellationToken);

        return contacts + oneOffs;
    }
}
