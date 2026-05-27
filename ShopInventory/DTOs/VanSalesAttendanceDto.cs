using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesAttendanceRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("customer_id")]
    public int? CustomerId { get; set; }

    [JsonPropertyName("van")]
    public string Van { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public string Latitude { get; set; } = string.Empty;

    [JsonPropertyName("longitude")]
    public string Longitude { get; set; } = string.Empty;
}

public class VanSalesAttendanceCheckResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public VanSalesAttendanceApiRecord? Data { get; set; }
}

public class VanSalesAttendanceListResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public VanSalesAttendancePaginatedData? Data { get; set; }
}

public class VanSalesAttendanceByDateResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public List<VanSalesAttendanceApiRecord> Data { get; set; } = new();
}

public class VanSalesAttendanceStatusResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public VanSalesAttendanceStatusData Data { get; set; } = new();
}

public class VanSalesAttendancePaginatedData
{
    [JsonPropertyName("current_page")]
    public int CurrentPage { get; set; }

    [JsonPropertyName("data")]
    public List<VanSalesAttendanceApiRecord> Data { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("per_page")]
    public int PerPage { get; set; }
}

public class VanSalesAttendanceApiRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("user_id")]
    public int UserId { get; set; }

    [JsonPropertyName("customer_id")]
    public int CustomerId { get; set; }

    [JsonPropertyName("van")]
    public VanSalesAttendanceVanDto Van { get; set; } = new();

    [JsonPropertyName("latitude")]
    public string Latitude { get; set; } = string.Empty;

    [JsonPropertyName("longitude")]
    public string Longitude { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("customer")]
    public VanSalesAttendanceCustomerDto Customer { get; set; } = new();
}

public class VanSalesAttendanceVanDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("v_code")]
    public string VCode { get; set; } = string.Empty;

    [JsonPropertyName("v_bp_name")]
    public string VBusinessPartnerName { get; set; } = string.Empty;
}

public class VanSalesAttendanceCustomerDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("c_code")]
    public string CustomerCode { get; set; } = string.Empty;

    [JsonPropertyName("c_name")]
    public string CustomerName { get; set; } = string.Empty;

    [JsonPropertyName("c_address")]
    public string? CustomerAddress { get; set; }
}

public class VanSalesAttendanceStatusData
{
    [JsonPropertyName("has_open_checkins")]
    public bool HasOpenCheckins { get; set; }

    [JsonPropertyName("open_checkins")]
    public List<VanSalesAttendanceStatusCheckIn> OpenCheckins { get; set; } = new();
}

public class VanSalesAttendanceStatusCheckIn
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("user_id")]
    public int UserId { get; set; }

    [JsonPropertyName("customer_id")]
    public int CustomerId { get; set; }

    [JsonPropertyName("van")]
    public string Van { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public string Latitude { get; set; } = string.Empty;

    [JsonPropertyName("longitude")]
    public string Longitude { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("customer")]
    public VanSalesAttendanceCustomerDto Customer { get; set; } = new();
}