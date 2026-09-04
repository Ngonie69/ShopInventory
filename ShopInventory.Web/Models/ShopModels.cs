namespace ShopInventory.Web.Models;

/// <summary>
/// A retail shop and the selling identity its tills inherit.
/// </summary>
/// <remarks>
/// Mirrors the API's <c>ShopDto</c> by hand, as the other models in this folder do — the web project
/// takes no reference on the API project.
/// </remarks>
public class ShopDto
{
    public int Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string BusinessPartnerCode { get; set; } = string.Empty;

    public string WarehouseCode { get; set; } = string.Empty;

    public string? CostCentreCode { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Active accounts working this shop's till.</summary>
    public int AssignedOperatorCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    /// <summary>How a shop is named in a picker: the name people say, with the code to disambiguate.</summary>
    public string DisplayLabel => $"{Name} ({Code})";
}
