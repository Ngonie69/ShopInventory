using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>
/// Administrator alerts for customer documents, with a cooldown per condition kept in
/// <c>SystemConfigs</c> so it holds across passes and nodes.
/// </summary>
/// <remarks>
/// <para>
/// Addressed to the Admin role rather than broadcast: <c>CreateSystemAlertAsync</c> would put a
/// gateway fault on every cashier's and manager's bell. And never one per document — invoices are
/// raised all day, and a per-invoice alert is how the bell's recent list gets flooded until it shows
/// nothing else.
/// </para>
/// <para>
/// Works in a scope of its own, so saving the cooldown can never also save half-finished changes
/// the delivery pass is holding on its own context.
/// </para>
/// </remarks>
public sealed class CustomerDocumentAlerts(
    IServiceScopeFactory scopeFactory,
    IOptions<CustomerDocumentDeliverySettings> options,
    ILogger<CustomerDocumentAlerts> logger) : ICustomerDocumentAlerts
{
    public const string ActionUrl = "/whatsapp-deliveries";

    public async Task RaiseAsync(string condition, string title, string message, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

            var now = DateTime.UtcNow;
            var cooldown = TimeSpan.FromHours(Math.Max(1, options.Value.AlertCooldownHours));

            var row = await context.SystemConfigs
                .AsTracking()
                .FirstOrDefaultAsync(config => config.Key == CustomerDocumentDeliveryKeys.AlertState, cancellationToken);

            var state = Read(row?.Value);
            if (state.TryGetValue(condition, out var lastRaised) && now - lastRaised < cooldown)
            {
                return;
            }

            state[condition] = now;

            await CustomerDocumentDeliveryKeys.StageAsync(
                context,
                CustomerDocumentDeliveryKeys.AlertState,
                "json",
                JsonSerializer.Serialize(state),
                "When each customer-document alert was last raised, so it is not raised again within the cooldown.",
                isEditable: false,
                cancellationToken);

            await context.SaveChangesAsync(cancellationToken);

            await notifications.CreateNotificationAsync(new CreateNotificationRequest
            {
                Title = title,
                Message = message,
                Type = "Warning",
                Category = "System",
                EntityType = "CustomerDocumentDelivery",
                ActionUrl = ActionUrl,
                TargetRole = "Admin"
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not raise the customer-document alert {Condition}: {Title}", condition, title);
        }
    }

    private static Dictionary<string, DateTime> Read(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(value);
            return parsed is null
                ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DateTime>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
