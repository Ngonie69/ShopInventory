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

    [JsonPropertyName("assigned_cost_centre_code")]
    public string? AssignedCostCentreCode { get; set; }
}