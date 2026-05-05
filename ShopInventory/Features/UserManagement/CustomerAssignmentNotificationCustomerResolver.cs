using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement;

internal static class CustomerAssignmentNotificationCustomerResolver
{
    public static async Task<IReadOnlyDictionary<string, string>> ResolveNamesAsync(
        IBusinessPartnerService businessPartnerService,
        IEnumerable<string> customerCodes,
        CancellationToken cancellationToken)
    {
        var normalizedCodes = customerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedCodes.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var namesByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var customerCode in normalizedCodes)
        {
            var businessPartner = await businessPartnerService.GetBusinessPartnerByCodeAsync(customerCode, cancellationToken);
            var customerName = businessPartner?.CardName?.Trim();

            if (!string.IsNullOrWhiteSpace(customerName))
            {
                namesByCode[customerCode] = customerName;
            }
        }

        return namesByCode;
    }
}