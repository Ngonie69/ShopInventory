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
    public int? PriceListNum { get; set; }
    public string? PriceListName { get; set; }
    public int? PayTermGrpCode { get; set; }
    public string? Channel { get; set; }

    // Display helper
    public string DisplayName => $"{CardCode} - {CardName}";
}

public class PaymentTermsDto
{
    public int GroupNumber { get; set; }
    public string? PaymentTermsGroupName { get; set; }
    public int NumberOfAdditionalDays { get; set; }
    public int NumberOfAdditionalMonths { get; set; }
}

public class BusinessPartnerListResponse
{
    public int TotalCount { get; set; }
    public List<BusinessPartnerDto>? BusinessPartners { get; set; }
}

/// <summary>
/// One of SAP's business partner groups. Mirrors the API's <c>BusinessPartnerGroupDto</c>.
/// </summary>
/// <remarks>
/// <see cref="Code"/> is an int here and a string on <see cref="BusinessPartnerDto.GroupCode"/>,
/// which is how SAP hands each of them over. Compare them as text after trimming, never by parsing
/// one into the other.
/// </remarks>
public class BusinessPartnerGroupDto
{
    public int Code { get; set; }
    public string? Name { get; set; }
}

/// <summary>Mirrors the API's <c>BusinessPartnerGroupsListResponseDto</c>.</summary>
public class BusinessPartnerGroupsResponse
{
    public int Count { get; set; }
    public List<BusinessPartnerGroupDto>? Groups { get; set; }
}
