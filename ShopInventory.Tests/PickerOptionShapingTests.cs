using ShopInventory.Web.Components;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The shaping behind the /shops and /user-management pickers.
///
/// The rule these pin: a picker row's value is the SAP code, so every judgement about which rows
/// exist and in what order is a judgement about what an administrator can choose. Those judgements
/// were made three times over — once per warehouse field — before <see cref="WarehouseOptions"/> and
/// <see cref="CostCentreOptions"/> put them in one place, and the failure mode of making them twice
/// is silent: a row that quietly stops being offered, or a code that reads as unset because the list
/// no longer holds it. A test is the only thing that keeps them from drifting back apart.
/// </summary>
public sealed class PickerOptionShapingTests
{
    private static WarehouseDto Warehouse(string? code, string? name = "A depot") =>
        new() { WarehouseCode = code, WarehouseName = name, IsActive = true };

    private static CostCentreDto Centre(string? code, string? name = "A centre", bool active = true) =>
        new() { CenterCode = code, CenterName = name, IsActive = active };

    [Fact]
    public void Warehouse_rows_carry_the_code_as_the_value_and_the_name_as_the_label()
    {
        var rows = WarehouseOptions.From([Warehouse("KEFGRS", "Kefalos Graniteside Shop")]);

        var row = Assert.Single(rows);
        Assert.Equal("KEFGRS", row.Value);
        Assert.Equal("Kefalos Graniteside Shop", row.Label);
    }

    [Fact]
    public void A_codeless_warehouse_is_dropped_because_the_row_could_not_be_chosen()
    {
        var rows = WarehouseOptions.From([Warehouse("KEFGRS"), Warehouse(null), Warehouse("   ")]);

        Assert.Equal(["KEFGRS"], rows.Select(row => row.Value));
    }

    [Fact]
    public void A_nameless_warehouse_falls_back_to_its_code_rather_than_drawing_blank()
    {
        var rows = WarehouseOptions.From([Warehouse("KEFGRS", null), Warehouse("CMH", "  ")]);

        Assert.Equal(["KEFGRS", "CMH"], rows.Select(row => row.Label));
    }

    /// <summary>
    /// The /user-management fields lead their list with a synthetic "Currently assigned" row for a
    /// code the warehouse master no longer offers. Sorting here would bury the one row the
    /// administrator most needs to see, so <c>From</c> keeps the caller's order and <c>ByCode</c> is
    /// the opt-in for a plain master list.
    /// </summary>
    [Fact]
    public void From_keeps_the_callers_order_and_ByCode_sorts()
    {
        WarehouseDto[] warehouses = [Warehouse("VAN010", "Currently assigned"), Warehouse("CMH"), Warehouse("KEFGRS")];

        Assert.Equal(["VAN010", "CMH", "KEFGRS"], WarehouseOptions.From(warehouses).Select(row => row.Value));
        Assert.Equal(["CMH", "KEFGRS", "VAN010"], WarehouseOptions.ByCode(warehouses).Select(row => row.Value));
    }

    [Fact]
    public void ByCode_sorts_without_regard_to_case_so_Centr_z_and_COR5000_do_not_split()
    {
        var rows = CostCentreOptions.ByCode([Centre("COR5000"), Centre("Centr_z"), Centre("FAC3700")]);

        Assert.Equal(["Centr_z", "COR5000", "FAC3700"], rows.Select(row => row.Value));
    }

    /// <summary>
    /// Marked, not dropped. An inactive centre is already on shops, and a row missing from the list
    /// is how a field that was set reads as unset — the picker would fall through to its placeholder
    /// and an administrator editing something else would save the blank over it.
    /// </summary>
    [Fact]
    public void An_inactive_cost_centre_is_kept_and_marked()
    {
        var rows = CostCentreOptions.ByCode([Centre("FAC3700", "Factory", active: false), Centre("GEM4000", "General")]);

        Assert.Equal("Inactive", rows.Single(row => row.Value == "FAC3700").Hint);
        Assert.Null(rows.Single(row => row.Value == "GEM4000").Hint);
    }

    [Fact]
    public void A_codeless_cost_centre_is_dropped_and_a_nameless_one_falls_back_to_its_code()
    {
        var rows = CostCentreOptions.ByCode([Centre(null), Centre("GEM4000", null)]);

        var row = Assert.Single(rows);
        Assert.Equal("GEM4000", row.Value);
        Assert.Equal("GEM4000", row.Label);
    }

    [Fact]
    public void A_null_list_shapes_to_no_rows_rather_than_throwing()
    {
        Assert.Empty(WarehouseOptions.From(null));
        Assert.Empty(WarehouseOptions.ByCode(null));
        Assert.Empty(CostCentreOptions.ByCode(null));
    }
}
