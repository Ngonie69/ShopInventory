namespace ShopInventory.DTOs;

/// <summary>
/// A retail shop and the selling identity its tills inherit.
/// </summary>
/// <remarks>
/// <c>AssignedOperatorCount</c> is how many active accounts work this shop's till, carried so an
/// administrator can see what closing the shop would strand before trying it.
/// </remarks>
public sealed record ShopDto(
    int Id,
    string Code,
    string Name,
    string BusinessPartnerCode,
    string WarehouseCode,
    string? CostCentreCode,
    bool IsActive,
    int AssignedOperatorCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
