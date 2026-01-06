namespace ShopInventory.DTOs;

/// <summary>
/// DTO for Business Partner information
/// </summary>
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
}

/// <summary>
/// Response DTO for list of Business Partners
/// </summary>
public class BusinessPartnerListResponseDto
{
    public int TotalCount { get; set; }
    public List<BusinessPartnerDto>? BusinessPartners { get; set; }
}

/// <summary>
/// Response DTO for paginated Business Partners
/// </summary>
public class BusinessPartnerPagedResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public List<BusinessPartnerDto>? BusinessPartners { get; set; }
}
