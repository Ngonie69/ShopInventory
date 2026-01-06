namespace ShopInventory.Web.Models;

public class BusinessPartnerDto
{
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public string? CardType { get; set; }
    public string? GroupCode { get; set; }
    public string? Phone1 { get; set; }
    public string? Phone2 { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? Currency { get; set; }
    public decimal? Balance { get; set; }
    public bool IsActive { get; set; }

    // Display helper
    public string DisplayName => $"{CardCode} - {CardName}";
}

public class BusinessPartnerListResponse
{
    public int TotalCount { get; set; }
    public List<BusinessPartnerDto>? BusinessPartners { get; set; }
}
