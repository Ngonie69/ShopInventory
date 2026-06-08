namespace ShopInventory.Models;

/// <summary>
/// Canonical backend role definitions.
/// </summary>
public static class ApplicationRoles
{
    public const string Admin = "Admin";
    public const string ApiUser = "ApiUser";
    public const string Cashier = "Cashier";
    public const string StockController = "StockController";
    public const string DepotController = "DepotController";
    public const string Manager = "Manager";
    public const string PodOperator = "PodOperator";
    public const string Operator = "Operator";
    public const string Driver = "Driver";
    public const string Merchandiser = "Merchandiser";
    public const string SalesRep = "SalesRep";
    public const string MerchandiserPurchaseOrderViewer = "MerchandiserPurchaseOrderViewer";
    public const string Lab = "Lab";

    // Legacy compatibility roles retained for existing records and workflows.
    public const string User = "User";
    public const string ReadOnly = "ReadOnly";
    public const string Adr = "ADR";
    public const string Sales = "Sales";

    // Exposed for normal user creation flows. Operator remains runtime-supported but is not
    // surfaced from the standard user-role catalog until its management UX is normalized.
    public static readonly string[] AssignableRoles =
    [
        Admin,
        Manager,
        Cashier,
        StockController,
        DepotController,
        PodOperator,
        Driver,
        Merchandiser,
        SalesRep,
        MerchandiserPurchaseOrderViewer,
        Lab
    ];

    // Roles that can continue to exist on managed users during compatibility cleanup.
    public static readonly string[] RetainableManagedRoles =
    [
        Admin,
        Manager,
        Cashier,
        StockController,
        DepotController,
        PodOperator,
        Operator,
        Driver,
        Merchandiser,
        SalesRep,
        MerchandiserPurchaseOrderViewer,
        Lab,
        User,
        ReadOnly,
        Adr,
        Sales
    ];

    public static readonly string[] ApiAccessRoles =
    [
        Admin,
        ApiUser,
        User,
        Cashier,
        StockController,
        DepotController,
        Manager,
        PodOperator,
        Driver,
        Merchandiser,
        SalesRep,
        MerchandiserPurchaseOrderViewer,
        Lab,
        Adr,
        Sales
    ];

    public static readonly string[] ApiAccessWithOperatorRoles =
    [
        Admin,
        ApiUser,
        User,
        Cashier,
        StockController,
        DepotController,
        Manager,
        PodOperator,
        Operator,
        Driver,
        Merchandiser,
        SalesRep,
        MerchandiserPurchaseOrderViewer,
        Lab,
        Adr,
        Sales
    ];

    public static readonly string[] ScopedPodViewerRoles =
    [
        PodOperator,
        Operator
    ];

    public static readonly string[] DriverScopedRoles =
    [
        Driver,
        PodOperator
    ];

    public static readonly string[] LegacyVanSalesRoles =
    [
        Adr,
        Sales
    ];

    public static bool IsAssignableRole(string? role) => Contains(AssignableRoles, role);

    public static bool IsRetainableManagedRole(string? role) => Contains(RetainableManagedRoles, role);

    public static bool CanAssignOrRetainManagedRole(string? requestedRole, string? currentRole)
    {
        if (string.IsNullOrWhiteSpace(requestedRole))
        {
            return false;
        }

        return IsAssignableRole(requestedRole) ||
               (IsRetainableManagedRole(currentRole) &&
                string.Equals(Normalize(requestedRole), Normalize(currentRole), StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsLegacyManagedRole(string? role)
        => Contains([User, ReadOnly, Adr, Sales], role);

    public static bool RequiresWarehouseAssignments(string? role)
        => Contains([StockController, DepotController, Adr, Sales], role);

    public static bool SupportsWarehouseAssignments(string? role)
        => Contains([StockController, DepotController, Merchandiser, Adr, Sales], role);

    public static bool RequiresCustomerAssignments(string? role)
        => Contains([Merchandiser], role);

    public static bool SupportsCustomerAssignments(string? role)
        => Contains([Merchandiser, Driver, PodOperator], role);

    public static bool RequiresAssignedSection(string? role)
        => Contains([Driver, PodOperator, Operator], role);

    public static bool UsesBlanketMobileScope(string? role)
        => Contains(DriverScopedRoles, role);

    public static bool UsesLegacyRouteCustomerScope(string? role)
        => Contains(LegacyVanSalesRoles, role);

    public static bool RequiresAssignedBusinessPartnerCode(string? role)
        => UsesLegacyRouteCustomerScope(role);

    public static bool RequiresAssignedCostCentreCode(string? role)
        => UsesLegacyRouteCustomerScope(role);

    public static string DescribeAssignableRoles() => string.Join(", ", AssignableRoles);

    private static bool Contains(IEnumerable<string> roles, string? role)
    {
        var normalizedRole = Normalize(role);
        return normalizedRole is not null &&
               roles.Contains(normalizedRole, StringComparer.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? role)
        => string.IsNullOrWhiteSpace(role) ? null : role.Trim();
}
