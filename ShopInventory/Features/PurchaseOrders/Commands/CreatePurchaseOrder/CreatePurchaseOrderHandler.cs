using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Commands.CreatePurchaseOrder;

public sealed class CreatePurchaseOrderHandler(
    IPurchaseOrderService purchaseOrderService,
    IAuditService auditService,
    INotificationService notificationService,
    ILogger<CreatePurchaseOrderHandler> logger
) : IRequestHandler<CreatePurchaseOrderCommand, ErrorOr<PurchaseOrderDto>>
{
    public async Task<ErrorOr<PurchaseOrderDto>> Handle(
        CreatePurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await purchaseOrderService.CreateAsync(command.Request, command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.CreatePurchaseOrder, "PurchaseOrder", order.Id.ToString(), $"Purchase order created for {command.Request.CardCode}", true); } catch { }

            try
            {
                var supplierDisplay = BuildBusinessPartnerDisplay(order.CardCode, order.CardName);
                var totalDisplay = BuildMoneyDisplay(order.Currency, order.DocTotal);

                await notificationService.CreateNotificationAsync(
                    ModuleNotificationFactory.CreateBroadcastNotification(
                        $"Purchase Order Created: {order.OrderNumber}",
                        $"Purchase order {order.OrderNumber} for {supplierDisplay} totaling {totalDisplay} was created successfully.",
                        "Success",
                        "PurchaseOrder",
                        "PurchaseOrder",
                        order.SAPDocEntry?.ToString() ?? order.Id.ToString(),
                        "/purchase-orders",
                        new Dictionary<string, string>
                        {
                            ["purchaseOrderId"] = order.Id.ToString(),
                            ["orderNumber"] = order.OrderNumber,
                            ["sapDocEntry"] = order.SAPDocEntry?.ToString() ?? string.Empty,
                            ["sapDocNum"] = order.SAPDocNum?.ToString() ?? string.Empty,
                            ["cardCode"] = order.CardCode,
                            ["cardName"] = order.CardName ?? string.Empty,
                            ["currency"] = order.Currency ?? string.Empty,
                            ["docTotal"] = order.DocTotal.ToString("N2"),
                            ["status"] = order.Status.ToString()
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish purchase order notification for order {OrderId}", order.Id);
            }

            return order;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase order");
            return Errors.PurchaseOrder.CreationFailed(ex.Message);
        }
    }

    private static string BuildBusinessPartnerDisplay(string? cardCode, string? cardName)
    {
        var normalizedCode = cardCode?.Trim();
        var normalizedName = cardName?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return normalizedCode ?? "unknown supplier";
        }

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return normalizedName;
        }

        return $"{normalizedCode} - {normalizedName}";
    }

    private static string BuildMoneyDisplay(string? currency, decimal total)
        => string.IsNullOrWhiteSpace(currency)
            ? total.ToString("N2")
            : $"{currency} {total:N2}";
}
