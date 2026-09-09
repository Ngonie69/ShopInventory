namespace ShopInventory.DTOs;

public class UpdateRouteCustomerRequest
{
    public string? AssignedBusinessPartnerCode { get; set; }

    public string? Code { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The family name, for a cart vendor. Omitted for a shop.</summary>
    public string? Surname { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    public string? VatNumber { get; set; }

    public bool IsActive { get; set; } = true;
}