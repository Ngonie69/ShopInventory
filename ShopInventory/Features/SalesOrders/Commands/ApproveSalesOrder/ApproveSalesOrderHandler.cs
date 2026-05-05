using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.ApproveSalesOrder;

public sealed class ApproveSalesOrderHandler(
    ISalesOrderService salesOrderService,
    IAuditService auditService,
    INotificationService notificationService,
    ILogger<ApproveSalesOrderHandler> logger
) : IRequestHandler<ApproveSalesOrderCommand, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        ApproveSalesOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await salesOrderService.ApproveAsync(command.Id, command.UserId, cancellationToken);
            if (order.Source == SalesOrderSource.Mobile && !string.IsNullOrWhiteSpace(order.CreatedByUserName))
            {
                try
                {
                    var notificationMessage = order.SAPDocNum.HasValue
                        ? $"Your mobile sales order {order.OrderNumber} for {order.CardName ?? order.CardCode} was approved and posted to SAP as document #{order.SAPDocNum}."
                        : $"Your mobile sales order {order.OrderNumber} for {order.CardName ?? order.CardCode} was approved successfully.";

                    await notificationService.CreateNotificationAsync(new CreateNotificationRequest
                    {
                        Title = $"Sales Order Approved: {order.OrderNumber}",
                        Message = notificationMessage,
                        Type = "Success",
                        Category = "SalesOrder",
                        EntityType = "SalesOrder",
                        EntityId = order.OrderNumber,
                        ActionUrl = "/mobile-drafts",
                        TargetUsername = order.CreatedByUserName,
                        Data = new Dictionary<string, string>
                        {
                            ["orderId"] = order.Id.ToString(),
                            ["orderNumber"] = order.OrderNumber,
                            ["cardCode"] = order.CardCode,
                            ["customerCode"] = order.CardCode,
                            ["customerName"] = order.CardName ?? order.CardCode,
                            ["status"] = order.Status.ToString(),
                            ["sapDocNum"] = order.SAPDocNum?.ToString() ?? string.Empty
                        }
                    }, cancellationToken);
                }
                catch
                {
                }
            }

            try
            {
                var auditMessage = order.IsSynced && order.SAPDocNum.HasValue
                    ? $"Sales order {command.Id} approved and posted to SAP as Doc #{order.SAPDocNum}"
                    : $"Sales order {command.Id} approved";
                await auditService.LogAsync(AuditActions.ApproveSalesOrder, "SalesOrder", command.Id.ToString(), auditMessage, true);
            }
            catch
            {
            }
            return order;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to approve sales order {OrderId} for user {UserId}", command.Id, command.UserId);
            return Errors.SalesOrder.InvalidOperation(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error approving sales order {OrderId} for user {UserId}", command.Id, command.UserId);

            SalesOrderDto? order = null;
            try
            {
                order = await salesOrderService.GetByIdFromLocalAsync(command.Id, cancellationToken);
            }
            catch (Exception lookupEx)
            {
                logger.LogWarning(lookupEx, "Failed to load sales order {OrderId} context for approval error formatting", command.Id);
            }

            return Errors.SalesOrder.ApprovalFailed(
                BuildApprovalFailureMessage(ex.GetBaseException().Message, order?.CardName, order?.CardCode));
        }
    }

    private static string BuildApprovalFailureMessage(
        string? rawMessage,
        string? customerName = null,
        string? customerCode = null)
    {
        var message = rawMessage?.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return "This sales order could not be approved right now. Please try again.";
        }

        const string createSalesOrderPrefix = "Failed to create sales order:";
        if (message.StartsWith(createSalesOrderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            message = message[createSalesOrderPrefix.Length..].Trim();
        }

        var separatorIndex = message.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0 && int.TryParse(message[..separatorIndex].Trim(), out _))
        {
            message = message[(separatorIndex + 3)..].Trim();
        }

        if (IsInactiveCustomerMessage(message))
        {
            var customerDisplayName = BuildCustomerDisplayName(customerName, customerCode);
            if (!string.IsNullOrWhiteSpace(customerDisplayName))
            {
                return $"This sales order could not be approved because customer {customerDisplayName} is inactive.";
            }
        }

        if (LooksLikeSerializedPayload(message))
        {
            return "This sales order could not be approved because SAP rejected the request. Please review the order details and try again.";
        }

        return $"This sales order could not be approved because {ToSentenceFragment(message)}.";
    }

    private static bool LooksLikeSerializedPayload(string message)
        => message.Contains('{', StringComparison.Ordinal) ||
           message.Contains('}', StringComparison.Ordinal) ||
           message.Contains("\"error\"", StringComparison.OrdinalIgnoreCase);

    private static bool IsInactiveCustomerMessage(string message)
        => message.Contains("customer", StringComparison.OrdinalIgnoreCase)
           && message.Contains("inactive", StringComparison.OrdinalIgnoreCase);

    private static string BuildCustomerDisplayName(string? customerName, string? customerCode)
    {
        var normalizedName = customerName?.Trim();
        var normalizedCode = customerCode?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return normalizedCode ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return normalizedName;
        }

        if (normalizedName.Contains(normalizedCode, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedName;
        }

        return $"{normalizedName} {normalizedCode}";
    }

    private static string ToSentenceFragment(string value)
    {
        var trimmedValue = value.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(trimmedValue))
        {
            return "the request could not be completed";
        }

        return trimmedValue.Length == 1
            ? trimmedValue.ToLowerInvariant()
            : char.ToLowerInvariant(trimmedValue[0]) + trimmedValue[1..];
    }
}
