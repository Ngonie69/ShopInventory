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

    // --- Sent by handsets from the release that fixed offline capture; absent from older ones ---
    //
    // Every field below is optional and the server behaves as it always did without them, because a
    // van in the field runs whatever build it was last given and a release reaches the fleet slowly.
    // An old handset still checks in; it just cannot say when, so the server stamps its own clock as
    // it used to.

    /// <summary>
    /// When the rep tapped, as a CAT wall clock with no offset (<c>2026-08-12T09:04:11</c>) — the same
    /// convention the offline sales upload uses for <c>sold_at</c>. Absent on a live check-in, where
    /// the server's clock is the better answer anyway.
    /// </summary>
    [JsonPropertyName("captured_at")]
    public string? CapturedAt { get; set; }

    /// <summary>
    /// The handset's own id for this event. A sync that reaches the server but loses the reply resends
    /// it, and this is what stops the second attempt becoming a second visit.
    /// </summary>
    [JsonPropertyName("client_reference")]
    public string? ClientReference { get; set; }

    /// <summary>The fix's uncertainty in metres, where the platform reported one.</summary>
    [JsonPropertyName("accuracy_metres")]
    public double? AccuracyMetres { get; set; }

    /// <summary><c>Gps</c>, <c>LastKnown</c> or <c>None</c> — how far the coordinates can be trusted.</summary>
    [JsonPropertyName("location_source")]
    public string? LocationSource { get; set; }

    /// <summary>Why there are no coordinates, on the rare event recorded without a fix.</summary>
    [JsonPropertyName("location_unavailable_reason")]
    public string? LocationUnavailableReason { get; set; }
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