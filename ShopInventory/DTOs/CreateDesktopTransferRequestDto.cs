using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class CreateDesktopTransferRequestDto
{
    /// <summary>
    /// The warehouse being asked for the stock. Optional for an account on a shop, whose shop decides
    /// it and overrides this; required of everyone else. See <c>ShopSupplyingWarehouse.ResolveForRequest</c>.
    /// </summary>
    public string? FromWarehouse { get; set; }

    [Required(ErrorMessage = "Destination warehouse is required")]
    public string ToWarehouse { get; set; } = string.Empty;

    public string? DocDate { get; set; }
    public string? DueDate { get; set; }
    public string? Comments { get; set; }
    public string? RequesterEmail { get; set; }
    public string? RequesterName { get; set; }
    public int? RequesterBranch { get; set; }
    public int? RequesterDepartment { get; set; }

    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1)]
    public List<CreateDesktopTransferRequestLineDto> Lines { get; set; } = new();
}
