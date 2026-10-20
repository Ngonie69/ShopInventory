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
