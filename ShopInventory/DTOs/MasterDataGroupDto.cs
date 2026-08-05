namespace ShopInventory.DTOs;

/// <summary>
/// One of SAP's item groups (OITB) — the group an item belongs to on its master record, and the
/// one Sales Analysis narrows an item selection by.
/// </summary>
/// <remarks>
/// Not to be confused with <c>U_ItemGroup</c>, the user-defined field this company also keeps and
/// which travels as <see cref="ProductDto.Category"/>. Sales Analysis reads the standard group, so
/// that is the one carried here.
/// </remarks>
public class ItemGroupDto
{
    /// <summary>SAP's <c>ItemGroups.Number</c>, which is what <c>Item.ItemsGroupCode</c> points at.</summary>
    public int Number { get; set; }

    public string? GroupName { get; set; }
}

/// <summary>
/// One of SAP's business partner groups (OCRG) — the group a partner belongs to, and the one Sales
/// Analysis narrows a customer selection by.
/// </summary>
public class BusinessPartnerGroupDto
{
    /// <summary>SAP's <c>BusinessPartnerGroups.Code</c>, which is what <c>BusinessPartner.GroupCode</c> points at.</summary>
    public int Code { get; set; }

    public string? Name { get; set; }
}

/// <summary>Response wrapper for the item group list.</summary>
public class ItemGroupsListResponseDto
{
    public int Count { get; set; }
    public List<ItemGroupDto>? Groups { get; set; }
}

/// <summary>Response wrapper for the business partner group list.</summary>
public class BusinessPartnerGroupsListResponseDto
{
    public int Count { get; set; }
    public List<BusinessPartnerGroupDto>? Groups { get; set; }
}
