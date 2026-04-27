namespace ShopInventory.Web.Models;

public sealed class ManagedMerchandiserAccountModel
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> AssignedWarehouseCodes { get; set; } = new();
    public string? AssignedWarehouseCode => AssignedWarehouseCodes.FirstOrDefault();
    public List<string> AssignedCustomerCodes { get; set; } = new();
}