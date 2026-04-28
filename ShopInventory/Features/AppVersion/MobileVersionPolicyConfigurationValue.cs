namespace ShopInventory.Features.AppVersion;

internal static class MobileVersionPolicyConfigurationValue
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal)
            ? null
            : trimmed;
    }
}