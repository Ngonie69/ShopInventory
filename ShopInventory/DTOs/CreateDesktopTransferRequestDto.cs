using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class CreateDesktopTransferRequestDto
{
    [Required(ErrorMessage = "Source warehouse is required")]
    public string FromWarehouse { get; set; } = string.Empty;

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
