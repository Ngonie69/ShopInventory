namespace ShopInventory.Features.FactoryTransfers;

/// <summary>
/// The factory's departments and the SAP warehouses on each side of them.
/// </summary>
/// <remarks>
/// <para>
/// Stock leaves production for dispatch through <see cref="HoldingWarehouseCode"/> (FSRW, "Factory
/// Stock Released Holding Warehouse"), which is how the floor already moves it: 358 production
/// transfers into FSRW and 419 out of it to dispatch between 1 September and 19 October 2026. A
/// transfer sits there until somebody on the receiving side accepts it, so stock that has left one
/// floor is never still counted on it.
/// </para>
/// <para>
/// FSRW is one warehouse shared by every department. Which department a unit in it belongs to is
/// known from the factory transfer that put it there, never from the warehouse.
/// </para>
/// <para>
/// Fixed in code rather than configuration: these are the company's real warehouse codes, they
/// change about as often as the factory floor does, and a configured list is bound by appending to
/// whatever a default already holds.
/// </para>
/// </remarks>
public static class FactoryDepartments
{
    public const string HoldingWarehouseCode = "FSRW";

    public static readonly IReadOnlyList<FactoryDepartment> All =
    [
        new("YOG", "Yoghurt", ["PROYG", "PROYG 2"], "DISPYOG"),
        new("MILK", "Market Milk", ["PROMM"], "DISPMILK"),
        new("ICE", "Ice Cream", ["PROIC"], "DISPICE"),
        new("CHEESE", "Cheese", ["PROCH", "PROPC", "PROCS"], "DISPCH")
    ];

    public static FactoryDepartment? Find(string? code)
        => All.FirstOrDefault(department => string.Equals(department.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The department a production or dispatch warehouse belongs to, and which side it is on.</summary>
    public static (FactoryDepartment Department, FactorySide Side)? Locate(string? warehouseCode)
    {
        var code = warehouseCode?.Trim();
        if (string.IsNullOrEmpty(code))
            return null;

        foreach (var department in All)
        {
            if (department.ProductionWarehouses.Contains(code, StringComparer.OrdinalIgnoreCase))
                return (department, FactorySide.Production);
            if (string.Equals(department.DispatchWarehouse, code, StringComparison.OrdinalIgnoreCase))
                return (department, FactorySide.Dispatch);
        }

        return null;
    }

    /// <summary>Every production and dispatch warehouse the module knows.</summary>
    public static IEnumerable<string> AllWarehouses
        => All.SelectMany(department => department.ProductionWarehouses.Append(department.DispatchWarehouse));

    /// <summary>
    /// The depots' transit warehouse. Between 1 September and 19 October 2026 dispatch moved 135 loads
    /// into it and 66 out to the depots, almost all dated the same day, because the factory posted both
    /// legs and the depot confirmed nothing. Here the depot posts the second one by accepting.
    /// </summary>
    public const string DepotHoldingWarehouseCode = "DEPOTRAN";

    /// <summary>
    /// The depots Distribution delivers to, each with the shop beside it. A delivery goes to the centre;
    /// stock may come back from either.
    /// </summary>
    public static readonly IReadOnlyList<FactoryDepot> Depots =
    [
        new("KEFGRC", "Graniteside", ["KEFGRS"]),
        new("CORMACH", "Machipisa", ["CORMACH2"]),
        new("KEFBYC", "Bulawayo", ["KEFBYS"])
    ];

    /// <summary>The dispatch warehouses a truck is loaded from.</summary>
    public static IReadOnlyList<string> DispatchWarehouses => All.Select(department => department.DispatchWarehouse).ToList();

    public static string? CanonicalDispatch(string? code)
        => DispatchWarehouses.FirstOrDefault(warehouse => string.Equals(warehouse, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The depot a centre code names, for a delivery's destination.</summary>
    public static FactoryDepot? FindDepotCentre(string? code)
        => Depots.FirstOrDefault(depot => string.Equals(depot.Centre, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where crates and buckets are washed. Between 1 September and 19 October 2026 it issued 61,000
    /// crates to PROYG 2, 5,400 to DISPYOG and 11,800 buckets to WAREH, and took back about 10,000 from
    /// production, dispatch, RETURNS, the depots and Cheeseman, every one posted with no count.
    /// </summary>
    public const string WashbayWarehouseCode = "WASHBAY";

    /// <summary>The department code every washbay movement is filed under.</summary>
    public const string WashbayDepartmentCode = "WASHBAY";

    /// <summary>The bucket store the washbay issues buckets to.</summary>
    public const string BucketStoreWarehouseCode = "WAREH";

    /// <summary>Where the washbay issues clean crates and buckets.</summary>
    public static IReadOnlyList<string> WashbayIssueDestinations =>
        All.SelectMany(department => department.ProductionWarehouses)
            .Concat(DispatchWarehouses)
            .Append(BucketStoreWarehouseCode)
            .ToList();

    /// <summary>
    /// Where the washbay takes dirty crates in from at its own count. Not the depots: their crates keep
    /// the direct transfer they use today.
    /// </summary>
    public static IReadOnlyList<string> WashbayReceiptSources =>
        All.SelectMany(department => department.ProductionWarehouses)
            .Concat(DispatchWarehouses)
            .Concat(["RETURNS", "CMH", "CMB", "CMTRANS", "CMFC"])
            .ToList();

    /// <summary>
    /// Crates (CRA…) and buckets (BUC…): the only things a washbay route carries, so finished goods can
    /// never be sent to or from the washbay.
    /// </summary>
    public static bool IsPackagingItem(string? itemCode)
    {
        var code = itemCode?.Trim();
        return !string.IsNullOrEmpty(code)
            && (code.StartsWith("CRA", StringComparison.OrdinalIgnoreCase) || code.StartsWith("BUC", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The department code every depot movement is filed under.</summary>
    public const string DepotDepartmentCode = "DEPOT";

    /// <summary>
    /// The name shown beside the direction: the department for a factory transfer, the depot for a
    /// delivery or a depot return — "Yoghurt release", "Graniteside delivery", "Machipisa return".
    /// </summary>
    public static string DisplayName(string departmentCode, string direction, string source, string destination)
    {
        if (direction == Models.Entities.FactoryTransferDirections.Delivery)
            return LocateDepotWarehouse(destination)?.Depot.Name ?? destination;
        if (direction == Models.Entities.FactoryTransferDirections.DepotReturn)
            return LocateDepotWarehouse(source)?.Depot.Name ?? source;
        if (Models.Entities.FactoryTransferDirections.IsDirect(direction))
            return "Washbay";
        return Find(departmentCode)?.Name ?? departmentCode;
    }

    public static string DirectionWord(string direction) => direction switch
    {
        Models.Entities.FactoryTransferDirections.Return or Models.Entities.FactoryTransferDirections.DepotReturn => "return",
        Models.Entities.FactoryTransferDirections.Delivery => "delivery",
        Models.Entities.FactoryTransferDirections.CrateIssue => "issue",
        Models.Entities.FactoryTransferDirections.CrateReceipt => "receipt",
        _ => "release"
    };

    /// <summary>The depot a centre or shop code belongs to, and that code's canonical spelling.</summary>
    public static (FactoryDepot Depot, string Warehouse)? LocateDepotWarehouse(string? code)
    {
        var trimmed = code?.Trim();
        foreach (var depot in Depots)
        {
            if (string.Equals(depot.Centre, trimmed, StringComparison.OrdinalIgnoreCase))
                return (depot, depot.Centre);
            var shop = depot.Shops.FirstOrDefault(item => string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase));
            if (shop is not null)
                return (depot, shop);
        }

        return null;
    }
}

public sealed record FactoryDepot(string Centre, string Name, IReadOnlyList<string> Shops)
{
    public IEnumerable<string> Warehouses => Shops.Prepend(Centre);
}

public sealed record FactoryDepartment(
    string Code,
    string Name,
    IReadOnlyList<string> ProductionWarehouses,
    string DispatchWarehouse)
{
    /// <summary>The canonical spelling of <paramref name="warehouseCode"/> if it is this department's.</summary>
    public string? Canonical(string? warehouseCode)
    {
        var code = warehouseCode?.Trim();
        if (string.Equals(DispatchWarehouse, code, StringComparison.OrdinalIgnoreCase))
            return DispatchWarehouse;
        return ProductionWarehouses.FirstOrDefault(warehouse => string.Equals(warehouse, code, StringComparison.OrdinalIgnoreCase));
    }
}

public enum FactorySide
{
    Production,
    Dispatch
}
