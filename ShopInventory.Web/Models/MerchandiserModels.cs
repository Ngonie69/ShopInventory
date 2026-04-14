using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

#region Merchandiser Product Models

public class MerchandiserProductDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("merchandiserUserId")]
    public Guid MerchandiserUserId { get; set; }

    [JsonPropertyName("itemCode")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("itemName")]
    public string? ItemName { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; set; }
}

public class MerchandiserProductListResponse
{
    [JsonPropertyName("merchandiserUserId")]
    public Guid MerchandiserUserId { get; set; }

    [JsonPropertyName("merchandiserName")]
    public string? MerchandiserName { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("activeCount")]
    public int ActiveCount { get; set; }

    [JsonPropertyName("products")]
    public List<MerchandiserProductDto> Products { get; set; } = new();
}

public class MerchandiserSummaryDto
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("totalProducts")]
    public int TotalProducts { get; set; }

    [JsonPropertyName("activeProducts")]
    public int ActiveProducts { get; set; }

    [JsonPropertyName("assignedCustomers")]
    public int AssignedCustomers { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public class SapSalesItemDto
{
    [JsonPropertyName("itemCode")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("itemName")]
    public string ItemName { get; set; } = string.Empty;

    [JsonPropertyName("itemGroup")]
    public string? ItemGroup { get; set; }
}

#endregion
