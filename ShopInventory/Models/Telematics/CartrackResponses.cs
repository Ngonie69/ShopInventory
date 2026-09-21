using System.Text.Json.Serialization;

namespace ShopInventory.Models.Telematics;

/// <summary>
/// The Cartrack Fleet API's responses, exactly as they arrive.
/// </summary>
/// <remarks>
/// <para>
/// Every property carries an explicit <see cref="JsonPropertyNameAttribute"/> rather than
/// relying on a naming policy. The wire is snake_case, but not uniformly: <c>temp1</c> through
/// <c>temp4</c> have no separator, <c>recieved_ts</c> is misspelled at source, and
/// <c>custom_field_1</c> mixes both. A policy would map most of them and silently leave the rest
/// null, which reads as a van that reported nothing.
/// </para>
/// <para>
/// Timestamps are strings here, not <c>DateTime</c>. They arrive as
/// <c>2026-09-18 06:52:03+02</c> — an offset, but a space instead of the <c>T</c>, so
/// <c>DateTime</c> binding is a coin toss and a local-kind parse would be right on a developer's
/// machine and wrong on the IIS box. They are parsed deliberately, once, in
/// <see cref="Services.Telematics.CartrackTime"/>.
/// </para>
/// </remarks>
public sealed class CartrackEnvelope<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("meta")]
    public CartrackPagination? Meta { get; set; }
}

public sealed class CartrackPagination
{
    [JsonPropertyName("current_page")]
    public int CurrentPage { get; set; }

    [JsonPropertyName("last_page")]
    public int LastPage { get; set; }

    [JsonPropertyName("per_page")]
    public int PerPage { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>One vehicle in the fleet, with what its tracker can actually measure.</summary>
public sealed class CartrackVehicle
{
    [JsonPropertyName("vehicle_id")]
    public long? VehicleId { get; set; }

    [JsonPropertyName("registration")]
    public string? Registration { get; set; }

    [JsonPropertyName("chassis_number")]
    public string? ChassisNumber { get; set; }

    [JsonPropertyName("terminal_serial")]
    public string? TerminalSerial { get; set; }

    /// <summary>
    /// The customer's own name for the vehicle — this fleet uses "306_AFQ9644", a yard number
    /// joined to the plate. Spelled <c>vehicle_name</c> on the wire, not the
    /// <c>client_vehicle_name</c> the published spec gives; the spec's name binds to nothing and
    /// leaves every vehicle looking unnamed.
    /// </summary>
    [JsonPropertyName("vehicle_name")]
    public string? ClientVehicleName { get; set; }

    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("model_year")]
    public int? ModelYear { get; set; }

    /// <summary>In the workshop — the reason a day has no data, rather than a van that sat still.</summary>
    [JsonPropertyName("is_under_maintenance")]
    public bool? IsUnderMaintenance { get; set; }

    [JsonPropertyName("terminal_in_repair")]
    public bool? TerminalInRepair { get; set; }

    /// <summary>
    /// What is fitted. Note there is <b>no temperature entry</b>: Cartrack publish fuel and
    /// electric capability and nothing at all about the four temperature channels, so whether a
    /// van has a box probe can only be learned by seeing one report.
    /// </summary>
    [JsonPropertyName("sensors")]
    public CartrackVehicleSensors? Sensors { get; set; }
}

public sealed class CartrackVehicleSensors
{
    [JsonPropertyName("fuel_canbus_consumed")]
    public bool? FuelCanbusConsumed { get; set; }

    [JsonPropertyName("fuel_canbus_level")]
    public bool? FuelCanbusLevel { get; set; }

    [JsonPropertyName("fuel_analog_level")]
    public bool? FuelAnalogLevel { get; set; }

    [JsonPropertyName("electric_battery")]
    public bool? ElectricBattery { get; set; }

    [JsonPropertyName("electric_charging")]
    public bool? ElectricCharging { get; set; }
}

/// <summary>
/// A vehicle's day as Cartrack bucket it. Their bucket, not ours — which is why the rollup takes
/// only the durations from here and reads the ignition times out of the events window itself.
/// </summary>
public sealed class CartrackVehicleActivity
{
    [JsonPropertyName("vehicle_id")]
    public long? VehicleId { get; set; }

    [JsonPropertyName("registration")]
    public string? Registration { get; set; }

    [JsonPropertyName("first_ignition_on")]
    public string? FirstIgnitionOn { get; set; }

    [JsonPropertyName("last_ignition_off")]
    public string? LastIgnitionOff { get; set; }

    [JsonPropertyName("idle_time_seconds")]
    public int? IdleSeconds { get; set; }

    [JsonPropertyName("driving_time_seconds")]
    public int? DrivingSeconds { get; set; }
}

/// <summary>
/// One terminal event. This is the richest thing the API gives per call: an ignition event
/// carries where it happened, the odometer at the time and all four temperature channels.
/// </summary>
public sealed class CartrackVehicleEvent
{
    [JsonPropertyName("event_id")]
    public long? EventId { get; set; }

    [JsonPropertyName("registration")]
    public string? Registration { get; set; }

    [JsonPropertyName("event_description")]
    public string? EventDescription { get; set; }

    [JsonPropertyName("event_ts")]
    public string? EventTs { get; set; }

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("odometer")]
    public long? OdometerMetres { get; set; }

    [JsonPropertyName("speed")]
    public int? Speed { get; set; }

    [JsonPropertyName("temp1")]
    public decimal? Temp1 { get; set; }

    [JsonPropertyName("temp2")]
    public decimal? Temp2 { get; set; }

    [JsonPropertyName("temp3")]
    public decimal? Temp3 { get; set; }

    [JsonPropertyName("temp4")]
    public decimal? Temp4 { get; set; }
}

/// <summary>One temperature reading. Note Cartrack's own spelling of "received".</summary>
public sealed class CartrackTemperatureReading
{
    [JsonPropertyName("vehicle_id")]
    public long? VehicleId { get; set; }

    [JsonPropertyName("registration")]
    public string? Registration { get; set; }

    [JsonPropertyName("event_ts")]
    public string? EventTs { get; set; }

    [JsonPropertyName("recieved_ts")]
    public string? ReceivedTs { get; set; }

    [JsonPropertyName("temp1")]
    public decimal? Temp1 { get; set; }

    [JsonPropertyName("temp2")]
    public decimal? Temp2 { get; set; }

    [JsonPropertyName("temp3")]
    public decimal? Temp3 { get; set; }

    [JsonPropertyName("temp4")]
    public decimal? Temp4 { get; set; }
}

/// <summary>
/// The odometer over a window. Cartrack say this, not summed trip distances, is the correct
/// source for a day's kilometres.
/// </summary>
public sealed class CartrackOdometerSummary
{
    [JsonPropertyName("registration")]
    public string? Registration { get; set; }

    [JsonPropertyName("start_odometer_value")]
    public long? StartOdometerMetres { get; set; }

    [JsonPropertyName("end_odometer_value")]
    public long? EndOdometerMetres { get; set; }

    [JsonPropertyName("distance")]
    public long? DistanceMetres { get; set; }

    [JsonPropertyName("current_odometer_value")]
    public long? CurrentOdometerMetres { get; set; }

    /// <summary>The odometer was reset mid-window, so the distance across it means nothing.</summary>
    [JsonPropertyName("odometer_reset")]
    public bool? OdometerReset { get; set; }

    /// <summary>The tracker was swapped, for repair or otherwise, so the readings are not one series.</summary>
    [JsonPropertyName("terminal_has_changed")]
    public bool? TerminalHasChanged { get; set; }

    /// <summary>
    /// The last event Cartrack hold. A van out of coverage has not reported yet, so a window
    /// ending after this is incomplete rather than empty.
    /// </summary>
    [JsonPropertyName("last_event_ts")]
    public string? LatestEventTs { get; set; }
}

public sealed class CartrackFuelConsumed
{
    [JsonPropertyName("registration")]
    public string? Registration { get; set; }

    [JsonPropertyName("fuel_consumed")]
    public decimal? FuelConsumedLitres { get; set; }
}

public sealed class CartrackFuelLevel
{
    [JsonPropertyName("registration")]
    public string? Registration { get; set; }

    [JsonPropertyName("start_period")]
    public CartrackFuelLevelReading? Start { get; set; }

    [JsonPropertyName("end_period")]
    public CartrackFuelLevelReading? End { get; set; }

    [JsonPropertyName("estimated_fuel_used")]
    public decimal? EstimatedFuelUsedLitres { get; set; }

    /// <summary>An uncalibrated sensor produces no usable result, and Cartrack say so themselves.</summary>
    [JsonPropertyName("calibrated")]
    public bool? IsCalibrated { get; set; }
}

public sealed class CartrackFuelLevelReading
{
    [JsonPropertyName("liters")]
    public decimal? Litres { get; set; }

    /// <summary>Spelled "timestamp" here, unlike the "event_ts" every other endpoint uses.</summary>
    [JsonPropertyName("timestamp")]
    public string? EventTs { get; set; }

    /// <summary>
    /// Cartrack reprocess these. Recent readings are usually flagged inaccurate until more data
    /// arrives, so a figure taken from the last hour will move.
    /// </summary>
    [JsonPropertyName("accurate")]
    public bool? IsAccurate { get; set; }
}

/// <summary>A fill: how much, when, where, and at what odometer.</summary>
/// <remarks>
/// <para>
/// A sixth place the published spec is wrong. It documents <c>fuel_filled</c>, <c>event_ts</c>,
/// <c>odometer</c> and <c>location</c>; the server sends <c>fill_amount_litres</c>,
/// <c>fill_timestamp</c>, <c>fill_odometer</c> and <c>fill_location</c>. Bound to the spec's names,
/// every fill arrived with no litres, which read as a fill of nothing.
/// </para>
/// <para>
/// The server also returns the same fill more than once — observed on 2026-09-14, one 121.9 L
/// fill listed twice with identical timestamps and amounts. Callers de-duplicate.
/// </para>
/// </remarks>
public sealed class CartrackFuelFill
{
    [JsonPropertyName("registration")]
    public string? Registration { get; set; }

    [JsonPropertyName("fill_amount_litres")]
    public decimal? Litres { get; set; }

    [JsonPropertyName("fill_timestamp")]
    public string? EventTs { get; set; }

    /// <summary>In metres, like every other odometer figure this API returns.</summary>
    [JsonPropertyName("fill_odometer")]
    public long? OdometerMetres { get; set; }

    [JsonPropertyName("fill_location")]
    public string? Location { get; set; }

    /// <summary>Provisional until the provider reprocesses it, like the tank levels.</summary>
    [JsonPropertyName("accurate")]
    public bool? IsAccurate { get; set; }

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
}

/// <summary>
/// The live snapshot. One call returns this for the whole fleet, and it already carries the four
/// temperature channels — so the live strip costs no second request.
/// </summary>
public sealed class CartrackVehicleStatus
{
    [JsonPropertyName("vehicle_id")]
    public long? VehicleId { get; set; }

    [JsonPropertyName("registration")]
    public string? Registration { get; set; }

    [JsonPropertyName("event_ts")]
    public string? EventTs { get; set; }

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    /// <summary>Cartrack's own reverse geocode of the position.</summary>
    [JsonPropertyName("position_description")]
    public string? PositionDescription { get; set; }

    [JsonPropertyName("speed")]
    public int? SpeedKph { get; set; }

    [JsonPropertyName("bearing")]
    public int? Bearing { get; set; }

    [JsonPropertyName("ignition")]
    public bool? Ignition { get; set; }

    [JsonPropertyName("idling")]
    public bool? Idling { get; set; }

    [JsonPropertyName("odometer")]
    public long? OdometerMetres { get; set; }

    [JsonPropertyName("temp1")]
    public decimal? Temp1 { get; set; }

    [JsonPropertyName("temp2")]
    public decimal? Temp2 { get; set; }

    [JsonPropertyName("temp3")]
    public decimal? Temp3 { get; set; }

    [JsonPropertyName("temp4")]
    public decimal? Temp4 { get; set; }

    [JsonPropertyName("driver")]
    public CartrackDriver? Driver { get; set; }
}

public sealed class CartrackDriver
{
    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }
}
