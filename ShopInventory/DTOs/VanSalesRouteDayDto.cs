using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// What the handset sends when the rep leaves the depot.
///
/// Snake-cased and wrapped in the legacy status/message/data envelope like the rest of this
/// controller, because the handset speaks one dialect to this API and a second one here would be a
/// trap for the next person adding a route.
/// </summary>
public class VanSalesStartDayRequest
{
    /// <summary>Odometer on leaving. The only figure the rep must read off the dashboard.</summary>
    [JsonPropertyName("starting_mileage")]
    public int? StartingMileage { get; set; }

    /// <summary>
    /// The truck actually being driven. Null accepts the route's registered vehicle; a value overrides
    /// it for this day only.
    /// </summary>
    [JsonPropertyName("truck_reg_no")]
    public string? TruckRegNo { get; set; }

    /// <summary>Crates and pallets loaded out.</summary>
    [JsonPropertyName("rti_out")]
    public int? RtiOut { get; set; }

    [JsonPropertyName("latitude")]
    public string? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public string? Longitude { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// When the van actually left, CAT wall clock with no offset. This is the sheet's "Time out", so
    /// it must be the rep's own clock rather than the server's: a depot with no signal would otherwise
    /// record every departure as whenever the handset next found a tower.
    /// </summary>
    [JsonPropertyName("captured_at")]
    public string? CapturedAt { get; set; }

    [JsonPropertyName("client_reference")]
    public string? ClientReference { get; set; }
}

/// <summary>What the handset sends when the van is back at the depot.</summary>
public class VanSalesEndDayRequest
{
    [JsonPropertyName("closing_mileage")]
    public int? ClosingMileage { get; set; }

    [JsonPropertyName("rti_returned")]
    public int? RtiReturned { get; set; }

    [JsonPropertyName("declared_cash")]
    public decimal? DeclaredCash { get; set; }

    [JsonPropertyName("declared_ecocash")]
    public decimal? DeclaredEcocash { get; set; }

    [JsonPropertyName("declared_innbucks")]
    public decimal? DeclaredInnbucks { get; set; }

    [JsonPropertyName("declared_currency")]
    public string? DeclaredCurrency { get; set; }

    [JsonPropertyName("latitude")]
    public string? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public string? Longitude { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>The sheet's "Time In", in the rep's own clock. See the departure request.</summary>
    [JsonPropertyName("captured_at")]
    public string? CapturedAt { get; set; }

    [JsonPropertyName("client_reference")]
    public string? ClientReference { get; set; }
}

/// <summary>The day as the handset should show it.</summary>
public class VanSalesRouteDayDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary><c>yyyy-MM-dd</c> in CAT.</summary>
    [JsonPropertyName("trading_date")]
    public string TradingDate { get; set; } = string.Empty;

    [JsonPropertyName("route_code")]
    public string? RouteCode { get; set; }

    [JsonPropertyName("route_name")]
    public string? RouteName { get; set; }

    [JsonPropertyName("territory")]
    public string? Territory { get; set; }

    [JsonPropertyName("truck_reg_no")]
    public string? TruckRegNo { get; set; }

    /// <summary><c>yyyy-MM-ddTHH:mm:ss</c> in CAT — the same shape the handset sent.</summary>
    [JsonPropertyName("departed_at")]
    public string DepartedAt { get; set; } = string.Empty;

    [JsonPropertyName("starting_mileage")]
    public int? StartingMileage { get; set; }

    [JsonPropertyName("planned_customer_count")]
    public int PlannedCustomerCount { get; set; }

    [JsonPropertyName("returned_at")]
    public string? ReturnedAt { get; set; }

    [JsonPropertyName("closing_mileage")]
    public int? ClosingMileage { get; set; }

    [JsonPropertyName("kilometres_travelled")]
    public int? KilometresTravelled { get; set; }

    [JsonPropertyName("rti_out")]
    public int? RtiOut { get; set; }

    [JsonPropertyName("rti_returned")]
    public int? RtiReturned { get; set; }

    [JsonPropertyName("declared_cash")]
    public decimal? DeclaredCash { get; set; }

    [JsonPropertyName("declared_ecocash")]
    public decimal? DeclaredEcocash { get; set; }

    [JsonPropertyName("declared_innbucks")]
    public decimal? DeclaredInnbucks { get; set; }

    [JsonPropertyName("declared_currency")]
    public string? DeclaredCurrency { get; set; }

    [JsonPropertyName("is_closed")]
    public bool IsClosed { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// The envelope. <c>data</c> is null when there is no open day, which is the ordinary answer before
/// the rep has started and not an error.
/// </summary>
public class VanSalesRouteDayResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public VanSalesRouteDayDto? Data { get; set; }
}
