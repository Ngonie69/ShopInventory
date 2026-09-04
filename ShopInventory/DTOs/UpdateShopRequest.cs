namespace ShopInventory.DTOs;

/// <summary>
/// Changing a shop's details.
/// </summary>
/// <remarks>
/// Carries no <c>IsActive</c>. Opening and closing a shop goes through its own command, because
/// closing one has to be refused while operators are still assigned to it — a rule an edit form
/// silently flipping a checkbox would walk straight past. One writer for that column.
///
/// The code is not editable either. It is what sales history and reporting group on, so changing it
/// would silently re-parent a shop's past; a shop that needs a different code is a new shop.
/// </remarks>
public class UpdateShopRequest
{
    public string Name { get; set; } = string.Empty;

    public string BusinessPartnerCode { get; set; } = string.Empty;

    public string WarehouseCode { get; set; } = string.Empty;

    public string? CostCentreCode { get; set; }
}
