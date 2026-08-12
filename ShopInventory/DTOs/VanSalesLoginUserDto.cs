using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLoginUserDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("surname")]
    public string Surname { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("assigned_section")]
    public string? AssignedSection { get; set; }

    [JsonPropertyName("assigned_warehouse_code")]
    public string? AssignedWarehouseCode { get; set; }

    [JsonPropertyName("assigned_warehouse_codes")]
    public List<string> AssignedWarehouseCodes { get; set; } = new();

    [JsonPropertyName("assigned_customer_codes")]
    public List<string> AssignedCustomerCodes { get; set; } = new();

    [JsonPropertyName("assigned_business_partner_code")]
    public string? AssignedBusinessPartnerCode { get; set; }

    /// <summary>
    /// The assigned business partner's name — the rep's route, in words.
    /// </summary>
    /// <remarks>
    /// Empty when the user is not assigned to one, or when the business partner master could not be
    /// read; the handset falls back to showing the code. See <see cref="Features.VanSalesCompatibility.VanSalesRouteName"/>.
    /// </remarks>
    [JsonPropertyName("assigned_business_partner_name")]
    public string? AssignedBusinessPartnerName { get; set; }

    [JsonPropertyName("assigned_cost_centre_code")]
    public string? AssignedCostCentreCode { get; set; }

    /// <summary>
    /// The depot this van is loaded from — where its stock requests draw from.
    /// </summary>
    /// <remarks>
    /// The handset used to choose this itself from a hardcoded list of one, and sent a warehouse *name*
    /// rather than a code. It is assigned per van on the server now, so the handset only displays it.
    /// Empty when the account has no assignment, which the stock request endpoint rejects outright.
    /// </remarks>
    [JsonPropertyName("supplying_warehouse_code")]
    public string? SupplyingWarehouseCode { get; set; }
}