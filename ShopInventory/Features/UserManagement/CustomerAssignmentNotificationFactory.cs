using ShopInventory.DTOs;

namespace ShopInventory.Features.UserManagement;

internal static class CustomerAssignmentNotificationFactory
{
    public static CreateNotificationRequest? CreateForUser(
        string username,
        string role,
        IReadOnlyCollection<string> customerCodes,
        IReadOnlyDictionary<string, string>? customerNamesByCode,
        bool isRemoval,
        Guid? targetUserId = null)
    {
        return CreateCore(role, customerCodes, customerNamesByCode, isRemoval, targetUserId, username, null);
    }

    public static CreateNotificationRequest? CreateForRole(
        string targetRole,
        string role,
        IReadOnlyCollection<string> customerCodes,
        IReadOnlyDictionary<string, string>? customerNamesByCode,
        bool isRemoval)
    {
        return CreateCore(role, customerCodes, customerNamesByCode, isRemoval, null, null, targetRole);
    }

    private static CreateNotificationRequest? CreateCore(
        string role,
        IReadOnlyCollection<string> customerCodes,
        IReadOnlyDictionary<string, string>? customerNamesByCode,
        bool isRemoval,
        Guid? targetUserId,
        string? targetUsername,
        string? targetRole)
    {
        var orderedCustomerCodes = customerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orderedCustomerCodes.Count == 0)
        {
            return null;
        }

        var isDriver = string.Equals(role, "Driver", StringComparison.OrdinalIgnoreCase);
        var isSingleCustomer = orderedCustomerCodes.Count == 1;
        var previewCustomerLabels = string.Join(", ", orderedCustomerCodes.Take(3).Select(code => GetCustomerLabel(code, customerNamesByCode)));
        var suffix = orderedCustomerCodes.Count > 3 ? ", ..." : string.Empty;
        var listLabel = isDriver ? "available shop list" : "customer list";
        var changeVerb = isRemoval ? "removed from" : "assigned to";

        var title = isDriver
            ? isRemoval
                ? isSingleCustomer ? "Shop removed" : "Shops removed"
                : isSingleCustomer ? "Shop assigned" : "Shops assigned"
            : isRemoval
                ? isSingleCustomer ? "Customer removed" : "Customers removed"
                : isSingleCustomer ? "Customer assigned" : "Customers assigned";

        var message = isSingleCustomer
            ? $"{GetCustomerLabel(orderedCustomerCodes[0], customerNamesByCode)} was {changeVerb} your {listLabel}."
            : $"{orderedCustomerCodes.Count} {(isDriver ? "shops" : "customers")} were {changeVerb} your {listLabel}: {previewCustomerLabels}{suffix}";

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["changeType"] = isRemoval ? "Removed" : "Assigned",
            ["assignmentCount"] = orderedCustomerCodes.Count.ToString(),
            ["assignmentRole"] = role
        };

        if (isSingleCustomer)
        {
            payload["customerCode"] = orderedCustomerCodes[0];
            payload["cardCode"] = orderedCustomerCodes[0];

            if (customerNamesByCode is not null &&
                customerNamesByCode.TryGetValue(orderedCustomerCodes[0], out var customerName) &&
                !string.IsNullOrWhiteSpace(customerName))
            {
                payload["customerName"] = customerName;
                payload["cardName"] = customerName;
            }
        }
        else
        {
            payload["customerCodes"] = string.Join(",", orderedCustomerCodes);

            var customerNames = orderedCustomerCodes
                .Select(code => customerNamesByCode is not null && customerNamesByCode.TryGetValue(code, out var customerName)
                    ? customerName
                    : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            if (customerNames.Count > 0)
            {
                payload["customerNames"] = string.Join("|", customerNames);
            }
        }

        return new CreateNotificationRequest
        {
            Title = title,
            Message = message,
            Type = isRemoval ? "Warning" : "Info",
            Category = isDriver ? "Security" : "Customer",
            EntityType = "BusinessPartner",
            EntityId = isSingleCustomer ? orderedCustomerCodes[0] : null,
            ActionUrl = isDriver ? null : "/customers",
            TargetUserId = targetUserId,
            TargetUsername = targetUsername,
            TargetRole = targetRole,
            Data = payload
        };
    }

    private static string GetCustomerLabel(
        string customerCode,
        IReadOnlyDictionary<string, string>? customerNamesByCode)
    {
        if (customerNamesByCode is not null &&
            customerNamesByCode.TryGetValue(customerCode, out var customerName) &&
            !string.IsNullOrWhiteSpace(customerName))
        {
            return customerName;
        }

        return customerCode;
    }
}