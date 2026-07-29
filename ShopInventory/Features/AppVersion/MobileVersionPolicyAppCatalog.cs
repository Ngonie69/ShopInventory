namespace ShopInventory.Features.AppVersion;

internal static class MobileVersionPolicyAppCatalog
{
    public const string CheesemanPolicyKey = "cheeseman-driver";
    public const string KefalosSalesOrderPolicyKey = "kefalos-so";
    public const string KefalosVanSalesPolicyKey = "kefalos-vansales";

    private static readonly Dictionary<string, string> PolicyKeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [CheesemanPolicyKey] = CheesemanPolicyKey,
        ["cheeseman"] = CheesemanPolicyKey,
        ["driver"] = CheesemanPolicyKey,
        ["podoperator"] = CheesemanPolicyKey,
        ["com.cheeseman.app"] = CheesemanPolicyKey,
        [KefalosSalesOrderPolicyKey] = KefalosSalesOrderPolicyKey,
        ["shopinventory-mobile"] = KefalosSalesOrderPolicyKey,
        ["shopinventory.mobile"] = KefalosSalesOrderPolicyKey,
        ["merchandiser"] = KefalosSalesOrderPolicyKey,
        ["com.kefalocheese.so"] = KefalosSalesOrderPolicyKey,
        [KefalosVanSalesPolicyKey] = KefalosVanSalesPolicyKey,
        ["kefalos-van-sales"] = KefalosVanSalesPolicyKey,
        ["vansales"] = KefalosVanSalesPolicyKey,
        ["kefalosvansales"] = KefalosVanSalesPolicyKey,
        ["com.kefalos.vansales"] = KefalosVanSalesPolicyKey
    };

    public static readonly string[] SupportedPolicyKeys = [CheesemanPolicyKey, KefalosSalesOrderPolicyKey, KefalosVanSalesPolicyKey];

    public static bool IsSupportedPolicyKey(string? appId) => TryResolvePolicyKey(appId, out _);

    public static bool TryResolvePolicyKey(string? appId, out string policyKey)
    {
        if (!string.IsNullOrWhiteSpace(appId) && PolicyKeyAliases.TryGetValue(appId.Trim(), out var resolvedPolicyKey))
        {
            policyKey = resolvedPolicyKey;
            return true;
        }

        policyKey = string.Empty;
        return false;
    }

    public static string GetDisplayName(string? policyKey) => policyKey switch
    {
        CheesemanPolicyKey => "Cheeseman Driver App",
        KefalosSalesOrderPolicyKey => "Kefalos SO App",
        KefalosVanSalesPolicyKey => "Kefalos Van Sales App",
        _ => "Mobile App"
    };

    public static string[] GetNotificationAudienceRoles(string? policyKey) => policyKey switch
    {
        CheesemanPolicyKey => ["Driver", "PodOperator"],
        KefalosSalesOrderPolicyKey => ["Merchandiser"],
        KefalosVanSalesPolicyKey => ["ADR", "Sales"],
        _ => []
    };
}