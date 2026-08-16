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

    /// <summary>
    /// A cart vendor operator. Invoices the vendors assigned to its business partner, cash only, and
    /// prints nothing — its sales fiscalise in the background rather than at a counter.
    /// </summary>
    public const string CartVendor = "CartVendor";

    // Legacy compatibility roles retained for existing records and workflows.
    public const string User = "User";
    public const string ReadOnly = "ReadOnly";
    public const string Adr = "ADR";
    public const string Sales = "Sales";

    // Exposed for normal user creation flows. Operator remains runtime-supported but is not
    // surfaced from the standard user-role catalog until its management UX is normalized.
    //
    // ADR and Sales are declared above as legacy because of the scope mechanism they use — the
    // route-customer scope, see UsesLegacyRouteCustomerScope — not because the roles are closed.
    // Van sales is a live, actively developed workflow and new vans need accounts, so both are
    // assignable. Only the /api/usermanagement create path can take a complete one: it is the
    // only request shape carrying the business partner, cost centre and supplying warehouse a
    // van account needs, and the other create paths reject these two roles for that reason.
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
        Lab,
        Adr,
        Sales,
        CartVendor
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
        Sales,
        CartVendor
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
        Sales,
        CartVendor
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
        Sales,
        CartVendor
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

    /// <summary>
    /// Roles that sell to a named customer from a fixed list of their own, rather than to whoever
    /// walks in.
    /// </summary>
    /// <remarks>
    /// Membership here is load-bearing: it is what makes a business partner and a cost centre
    /// required on the account, and what scopes the account to its own route customers. A role that
    /// sells this way and is left out silently gets an empty customer list.
    ///
    /// Named for what it does rather than for the van app it started in — <see cref="CartVendor"/>
    /// is not legacy and not a van.
    /// </remarks>
    public static readonly string[] RouteCustomerScopedRoles =
    [
        Adr,
        Sales,
        CartVendor
    ];

    /// <summary>
    /// Roles whose stock is loaded from a depot, and which therefore need that depot named on the
    /// account.
    /// </summary>
    /// <remarks>
    /// A subset of <see cref="RouteCustomerScopedRoles"/>, not the same list. A van is loaded at a
    /// depot before it goes out, and the handset cannot be trusted to pick which one. A cart vendor
    /// sells from its own business partner's warehouse and is never loaded from somewhere else, so
    /// requiring a supplying warehouse on that account would demand a value nothing reads.
    /// </remarks>
    public static readonly string[] DepotLoadedRoles =
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

    // CartVendor is here because SellingAccountResolver refuses to sell without exactly one assigned
    // warehouse. Leaving it out would let an account be created that passes every check at creation
    // and then cannot ring up a single sale.
    public static bool RequiresWarehouseAssignments(string? role)
        => Contains([StockController, DepotController, Adr, Sales, CartVendor], role);

    public static bool SupportsWarehouseAssignments(string? role)
        => Contains([StockController, DepotController, Merchandiser, Adr, Sales, CartVendor], role);

    public static bool RequiresCustomerAssignments(string? role)
        => Contains([Merchandiser], role);

    public static bool SupportsCustomerAssignments(string? role)
        => Contains([Merchandiser, Driver, PodOperator], role);

    public static bool RequiresAssignedSection(string? role)
        => Contains([Driver, PodOperator, Operator], role);

    public static bool UsesBlanketMobileScope(string? role)
        => Contains(DriverScopedRoles, role);

    public static bool UsesRouteCustomerScope(string? role)
        => Contains(RouteCustomerScopedRoles, role);

    /// <summary>
    /// The name this predicate had before the two role catalogues were separated.
    /// </summary>
    /// <remarks>
    /// Kept because callers on main use it, and it asks the route-customer question, which is what
    /// this list answers. It is not a straight rename: the old single list also decided whether a
    /// role needed a supplying warehouse, and that is now <see cref="DepotLoadedRoles"/>, because a
    /// cart vendor is scoped to route customers but loads from no depot. Anything still asking the
    /// depot question through this name would get the wrong answer, so there is nothing routed
    /// through it but the scope check.
    /// </remarks>
    public static bool UsesLegacyRouteCustomerScope(string? role)
        => UsesRouteCustomerScope(role);

    public static bool RequiresAssignedBusinessPartnerCode(string? role)
        => UsesRouteCustomerScope(role);

    public static bool RequiresAssignedCostCentreCode(string? role)
        => UsesRouteCustomerScope(role);

    /// <summary>
    /// Whether the role is loaded from a depot, and so needs the depot naming itself on the account.
    /// Only the van roles are: everyone else either issues transfer requests by hand, choosing a source
    /// each time, or never issues one at all.
    /// </summary>
    public static bool RequiresSupplyingWarehouseCode(string? role)
        => Contains(DepotLoadedRoles, role);

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
