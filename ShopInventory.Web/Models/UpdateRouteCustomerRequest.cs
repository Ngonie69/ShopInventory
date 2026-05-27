namespace ShopInventory.Web.Models;

public class UpdateRouteCustomerRequest
{
    public string? AssignedBusinessPartnerCode { get; set; }

    public string? Code { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    public string? VatNumber { get; set; }

    public bool IsActive { get; set; } = true;
}