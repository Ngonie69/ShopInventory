using System.Globalization;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Notifications;

internal static class WorkflowNotificationFactory
{
    private const string MobileSalesActionUrl = "/mobile-drafts";

    public static CreateNotificationRequest CreateRouteCustomerCreatedNotification(
        Guid? targetUserId,
        string targetUsername,
        string assignedBusinessPartnerCode,
        int routeCustomerId,
        string customerCode,
        string customerName)
    {
        var customerDisplay = ResolveCustomerDisplay(customerCode, customerName);

        return new CreateNotificationRequest
        {
            Title = $"New Route Customer: {customerDisplay}",
            Message = $"{customerDisplay} is now available for route {assignedBusinessPartnerCode}.",
            Type = "Info",
            Category = "Customer",
            EntityType = "RouteCustomer",
            EntityId = routeCustomerId.ToString(CultureInfo.InvariantCulture),
            ActionUrl = MobileSalesActionUrl,
            TargetUserId = targetUserId,
            TargetUsername = targetUsername,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["assignedBusinessPartnerCode"] = assignedBusinessPartnerCode,
                ["routeCustomerId"] = routeCustomerId.ToString(CultureInfo.InvariantCulture),
                ["customerCode"] = customerCode,
                ["customerName"] = customerName
            }
        };
    }

    public static CreateNotificationRequest CreateInvoiceCreatedNotification(
        Guid? targetUserId,
        string targetUsername,
        InvoiceDto invoice,
        string? reservationId,
        string actionUrl,
        FiscalizationResult? fiscalization)
    {
        var customerDisplay = ResolveCustomerDisplay(invoice.CardCode, invoice.CardName);
        var message = $"Your invoice for {customerDisplay} was created in SAP as document #{invoice.DocNum}.";

        if (fiscalization?.Queued == true)
        {
            message += " Fiscalization is running in the background.";
        }
        else if (fiscalization is { Success: false })
        {
            message += " Fiscalization could not be queued automatically and may need review.";
        }

        return new CreateNotificationRequest
        {
            Title = $"Invoice Created: #{invoice.DocNum}",
            Message = message,
            Type = "Success",
            Category = "Invoice",
            EntityType = "Invoice",
            EntityId = invoice.DocNum.ToString(CultureInfo.InvariantCulture),
            ActionUrl = actionUrl,
            TargetUserId = targetUserId,
            TargetUsername = targetUsername,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["docEntry"] = invoice.DocEntry.ToString(CultureInfo.InvariantCulture),
                ["docNum"] = invoice.DocNum.ToString(CultureInfo.InvariantCulture),
                ["cardCode"] = invoice.CardCode ?? string.Empty,
                ["cardName"] = invoice.CardName ?? string.Empty,
                ["reservationId"] = reservationId ?? string.Empty,
                ["fiscalizationQueued"] = (fiscalization?.Queued == true).ToString().ToLowerInvariant()
            }
        };
    }

    public static CreateNotificationRequest CreateInvoiceFiscalizationNotification(
        Guid? targetUserId,
        string targetUsername,
        InvoiceDto invoice,
        FiscalizationResult result,
        string actionUrl)
    {
        var customerDisplay = ResolveCustomerDisplay(invoice.CardCode, invoice.CardName);
        var title = result.Success
            ? $"Invoice Fiscalized: #{invoice.DocNum}"
            : $"Invoice Fiscalization Failed: #{invoice.DocNum}";
        var message = result.Success
            ? BuildSuccessMessage(invoice.DocNum, customerDisplay, result)
            : $"Invoice #{invoice.DocNum} for {customerDisplay} was created in SAP, but fiscalization failed: {result.Message ?? result.ErrorDetails ?? "Unknown error"}.";

        return new CreateNotificationRequest
        {
            Title = title,
            Message = message,
            Type = result.Success ? "Success" : "Warning",
            Category = "Invoice",
            EntityType = "Invoice",
            EntityId = invoice.DocNum.ToString(CultureInfo.InvariantCulture),
            ActionUrl = actionUrl,
            TargetUserId = targetUserId,
            TargetUsername = targetUsername,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["docEntry"] = invoice.DocEntry.ToString(CultureInfo.InvariantCulture),
                ["docNum"] = invoice.DocNum.ToString(CultureInfo.InvariantCulture),
                ["cardCode"] = invoice.CardCode ?? string.Empty,
                ["cardName"] = invoice.CardName ?? string.Empty,
                ["receiptGlobalNo"] = result.ReceiptGlobalNo ?? string.Empty,
                ["verificationCode"] = result.VerificationCode ?? string.Empty,
                ["deviceSerial"] = result.DeviceSerial ?? string.Empty,
                ["status"] = result.Success ? "Success" : "Failed"
            }
        };
    }

    private static string ResolveCustomerDisplay(string? customerCode, string? customerName)
    {
        var normalizedCustomerCode = string.IsNullOrWhiteSpace(customerCode)
            ? "Customer"
            : customerCode.Trim();

        return string.IsNullOrWhiteSpace(customerName)
            ? normalizedCustomerCode
            : string.Equals(normalizedCustomerCode, customerName, StringComparison.OrdinalIgnoreCase)
                ? customerName
                : $"{customerName} ({normalizedCustomerCode})";
    }

    private static string BuildSuccessMessage(int docNum, string customerDisplay, FiscalizationResult result)
    {
        var receiptToken = string.IsNullOrWhiteSpace(result.ReceiptGlobalNo)
            ? string.Empty
            : $" Receipt #{result.ReceiptGlobalNo}.";

        return $"Invoice #{docNum} for {customerDisplay} was fiscalized successfully.{receiptToken}";
    }
}