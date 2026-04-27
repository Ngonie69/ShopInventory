namespace ShopInventory.DTOs;

public sealed class UpdateMerchandiserAssignedCustomersRequest
{
    public List<string> AssignedWarehouseCodes { get; set; } = new();
    public List<string> AssignedCustomerCodes { get; set; } = new();
}