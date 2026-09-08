namespace ShopInventory.DTOs;

public class RouteCustomerDto
{
    public int Id { get; set; }

    public string AssignedBusinessPartnerCode { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The family name, for a cart vendor. Null for a route customer that is a shop.</summary>
    public string? Surname { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    public string? VatNumber { get; set; }

    public bool IsActive { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public string? CreatedByUserName { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}