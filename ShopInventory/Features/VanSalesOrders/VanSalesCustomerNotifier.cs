using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesOrders;

/// <inheritdoc />
public sealed class VanSalesCustomerNotifier(
    ApplicationDbContext context,
    IPushNotificationService push,
    ILogger<VanSalesCustomerNotifier> logger) : IVanSalesCustomerNotifier
{
    public async Task NotifyOrderStatusAsync(VanSalesOrderEntity order, CancellationToken cancellationToken)
    {
        try
        {
            var tokens = await context.VanSalesCustomerDevices
                .AsNoTracking()
                .Where(d => d.VanSalesCustomerAccountId == order.VanSalesCustomerAccountId && !d.IsRevoked)
                .Select(d => d.DeviceToken)
                .ToListAsync(cancellationToken);

            if (tokens.Count == 0)
            {
                return;
            }

            var (title, body) = Describe(order);

            // The data payload lets the app open straight to the order rather than the list. Values
            // are strings because FCM data payloads carry nothing else.
            var data = new Dictionary<string, string>
            {
                ["type"] = "vanSalesOrder",
                ["orderId"] = order.Id.ToString(),
                ["orderNumber"] = order.OrderNumber,
                ["status"] = order.Status.ToString()
            };

            var sent = await push.SendToDeviceTokensAsync(tokens, title, body, data, cancellationToken);

            logger.LogInformation(
                "Pushed {Status} for van sales order {OrderNumber} to {Sent} of {Total} device(s).",
                order.Status, order.OrderNumber, sent, tokens.Count);
        }
        catch (Exception ex)
        {
            // Swallowed deliberately. This is called after a delivery or a cancellation has already
            // been committed, and letting a push failure surface would undo work that actually
            // happened in the world.
            logger.LogWarning(
                ex,
                "Could not push the status of van sales order {OrderNumber}.",
                order.OrderNumber);
        }
    }

    /// <summary>
    /// The notification in the customer's words.
    /// </summary>
    /// <remarks>
    /// A short delivery says so, and says by how much is missing is a matter for the app — but the
    /// notification must not read like a completed delivery, because that is the one a shopkeeper
    /// would not open.
    /// </remarks>
    private static (string Title, string Body) Describe(VanSalesOrderEntity order) => order.Status switch
    {
        VanSalesOrderStatus.Accepted =>
            ("Order received", $"We have your order {order.OrderNumber}."),

        VanSalesOrderStatus.Fulfilled =>
            ("Order delivered", $"Order {order.OrderNumber} was delivered in full."),

        VanSalesOrderStatus.PartiallyFulfilled =>
            ("Order part delivered", $"Some items on order {order.OrderNumber} could not be delivered. Tap to see what arrived."),

        VanSalesOrderStatus.Cancelled =>
            ("Order cancelled", $"Order {order.OrderNumber} has been cancelled."),

        VanSalesOrderStatus.Expired =>
            ("Order not delivered", $"Order {order.OrderNumber} was not delivered. Please contact your sales representative."),

        _ => ("Order updated", $"Order {order.OrderNumber} has been updated.")
    };
}
