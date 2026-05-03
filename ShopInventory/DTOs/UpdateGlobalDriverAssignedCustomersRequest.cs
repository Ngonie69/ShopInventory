namespace ShopInventory.DTOs;

public sealed class UpdateGlobalDriverAssignedCustomersRequest
{
    public List<string> AssignedCustomerCodes { get; set; } = new();
}