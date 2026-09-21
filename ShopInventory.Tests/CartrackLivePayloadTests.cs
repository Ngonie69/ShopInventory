using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Services;
using ShopInventory.Services.Telematics;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The wire models against bodies captured from the live Cartrack account on 2026-09-20.
/// </summary>
/// <remarks>
/// <para>
/// Every payload below is verbatim, only trimmed to fewer rows. They exist because the published
/// OpenAPI document is wrong in four places, and each error read as missing data rather than as a
/// failure: <c>idle_time_seconds</c> is documented as <c>idle_time</c>, <c>last_event_ts</c> as
/// <c>latest_event_ts</c>, and the whole fuel-level response uses <c>start_period</c> /
/// <c>liters</c> / <c>accurate</c> / <c>calibrated</c> / <c>timestamp</c> where the spec says
/// <c>start</c> / <c>fuel_level</c> / <c>is_accurate</c> / <c>is_calibrated</c> / <c>event_ts</c>.
/// </para>
/// <para>
/// A mapping that silently nulls is the worst failure this integration has, because the report
/// cannot tell it from a van that did not move. These pin the names to what the server actually
/// sends.
/// </para>
/// </remarks>
public class CartrackLivePayloadTests
{
    private static (CartrackClient Client, StubHandler Handler) Build(string body)
    {
        var settings = new CartrackSettings
        {
            Enabled = true,
            Username = "u",
            Password = "p",
            MaxRetries = 0,
            PageSize = 500,
            MaxPages = 2
        };

        var handler = new StubHandler(body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://fleetapi-zw.cartrack.com/rest/") };
        var options = Options.Create(settings);

        return (new CartrackClient(http, options, new CartrackRateLimiter(options),
            NullLogger<CartrackClient>.Instance), handler);
    }

    [Fact]
    public async Task Activity_reports_the_durations_the_server_actually_sends()
    {
        // Captured: GET /vehicles/activity?filter[date]=2026-09-19
        const string body = """
        {"data":[{"vehicle_id":32173439,"registration":"AFQ9644",
          "chassis_number":"WDB9505372L818949",
          "first_ignition_on":"2026-09-19 07:40:14+02",
          "last_ignition_off":"2026-09-19 14:55:56+02",
          "idle_time_seconds":581,"driving_time_seconds":282,
          "total_working_hours":"00:14","total_break_hours":"23:45",
          "drivers":[{"driver_id":null,"first_name":null,"last_name":null}]}]}
        """;

        var (client, _) = Build(body);

        var activity = Assert.Single(await client.GetActivityAsync(
            new DateTime(2026, 9, 19), CancellationToken.None));

        Assert.Equal("AFQ9644", activity.Registration);
        Assert.Equal(581, activity.IdleSeconds);
        Assert.Equal(282, activity.DrivingSeconds);
        Assert.Equal(
            AuditService.FromCAT(new DateTime(2026, 9, 19, 7, 40, 14)),
            CartrackTime.ToUtc(activity.FirstIgnitionOn));
    }

    [Fact]
    public async Task The_odometer_last_event_field_is_read()
    {
        // Captured: GET /vehicles/ACQ3455/odometer. Values are metres — 67100 over the day, which
        // is the 67.1 km the vehicle actually ran.
        const string body = """
        {"data":{"vehicle_id":458248840,"registration":"ACQ3455",
          "last_event_ts":"2026-09-20 10:32:41+02","terminal_has_changed":false,
          "terminal_serial":"JL140159",
          "start_odometer_value":1045507100,"end_odometer_value":1045574200,
          "distance":67100,"odometer_reset":false,"current_odometer_value":1045604300}}
        """;

        var (client, _) = Build(body);

        var odometer = await client.GetOdometerAsync(
            "ACQ3455",
            new DateTime(2026, 9, 18, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 19, 22, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.NotNull(odometer);
        Assert.Equal(67_100, odometer!.DistanceMetres);
        Assert.Equal(1_045_507_100, odometer.StartOdometerMetres);
        Assert.False(odometer.OdometerReset);
        Assert.NotNull(CartrackTime.ToUtc(odometer.LatestEventTs));
    }

    [Fact]
    public async Task The_fuel_level_response_uses_entirely_different_names_from_the_spec()
    {
        // Captured: GET /fuel/level/AFQ9644. Note also that this endpoint's timestamps are ISO
        // with a T and a full offset, while every other endpoint uses a space and "+02".
        const string body = """
        {"data":{"start_period":{"liters":41.12,"accurate":false,"timestamp":"2026-09-19T07:40:14+02:00"},
          "end_period":{"liters":41.08,"accurate":false,"timestamp":"2026-09-19T14:55:55+02:00"},
          "estimated_fuel_used":0.0412293558,"calibrated":true}}
        """;

        var (client, _) = Build(body);

        var level = await client.GetFuelLevelAsync(
            "AFQ9644",
            new DateTime(2026, 9, 18, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 19, 22, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.NotNull(level);
        Assert.True(level!.IsCalibrated);
        Assert.Equal(41.12m, level.Start?.Litres);
        Assert.Equal(41.08m, level.End?.Litres);

        // Both readings come back flagged inaccurate on a day-old window, which is the documented
        // behaviour and the reason the report must show the caveat rather than the number alone.
        Assert.False(level.Start?.IsAccurate);
        Assert.False(level.End?.IsAccurate);

        Assert.Equal(
            AuditService.FromCAT(new DateTime(2026, 9, 19, 7, 40, 14)),
            CartrackTime.ToUtc(level.Start?.EventTs));
    }

    [Fact]
    public async Task A_fill_is_read_from_the_fill_prefixed_fields_the_server_sends()
    {
        // Captured 2026-09-21: GET /fuel/fills/AFQ9644 for 14 Sep. The spec's fuel_filled /
        // event_ts / odometer / location bind to nothing, and every fill read as zero litres.
        // The same fill is listed twice, verbatim — kept here because de-duplicating it is the
        // caller's job and this body is what the caller is handed. Chassis number redacted.
        const string body = """
        {"data":[{"vehicle_id":32173439,"registration":"AFQ9644","chassis_number":"REDACTED","vehicle_description":null,
          "fill_amount_litres":121.90123760313844,"fill_timestamp":"2026-09-14 08:14:18+02","fill_odometer":386634593,
          "fill_location":"Prospect, Harare, Harare, Zimbabwe","latitude":-17.882225,"longitude":31.069748,"accurate":true},
          {"vehicle_id":32173439,"registration":"AFQ9644","chassis_number":"REDACTED","vehicle_description":null,
          "fill_amount_litres":121.90123760313844,"fill_timestamp":"2026-09-14 08:14:18+02","fill_odometer":386634593,
          "fill_location":"Prospect, Harare, Harare, Zimbabwe","latitude":-17.882225,"longitude":31.069748,"accurate":true}],
         "meta":{"from":1,"to":2,"current_page":1,"per_page":50,"last_page":1,"total":2,"calibrated":true}}
        """;

        var (client, _) = Build(body);

        var fills = await client.GetFuelFillsAsync(
            "AFQ9644",
            new DateTime(2026, 9, 13, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 14, 22, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Equal(2, fills.Count);

        var fill = fills[0];
        Assert.Equal(121.90123760313844m, fill.Litres);
        Assert.Equal(386634593L, fill.OdometerMetres);
        Assert.Equal("Prospect, Harare, Harare, Zimbabwe", fill.Location);
        Assert.True(fill.IsAccurate);
        Assert.Equal(
            AuditService.FromCAT(new DateTime(2026, 9, 14, 8, 14, 18)),
            CartrackTime.ToUtc(fill.EventTs));
    }

    [Fact]
    public async Task A_temperature_reading_keeps_cartracks_own_misspelling()
    {
        // Captured: GET /topics/vehicles/temperature?filter[registration]=AFQ9644. Only temp1 is
        // fitted on this fleet; the other three channels are present and null.
        const string body = """
        {"data":[{"vehicle_id":32173439,"registration":"AFQ9644","temp1":17.6,"temp2":null,
          "temp3":null,"temp4":null,"event_ts":"2026-09-19 19:37:07+02",
          "recieved_ts":"2026-09-19 19:38:32+02"}],
         "meta":{"from":1,"to":1,"current_page":1,"per_page":1,"last_page":1,"total":1}}
        """;

        var (client, _) = Build(body);

        var reading = Assert.Single(await client.GetTemperaturesAsync(
            new DateTime(2026, 9, 18, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 19, 22, 0, 0, DateTimeKind.Utc),
            "AFQ9644",
            CancellationToken.None));

        Assert.Equal(17.6m, reading.Temp1);
        Assert.Null(reading.Temp2);
        Assert.NotNull(CartrackTime.ToUtc(reading.ReceivedTs));
    }

    [Fact]
    public async Task A_vehicle_with_no_can_fuel_answers_with_an_empty_object_not_an_error()
    {
        // Captured: GET /fuel/consumed/AFQ9644 — this fleet has no CAN fuel, and the endpoint
        // answers 200 with {} rather than 404. It must read as "not reported", not as a failure.
        var (client, _) = Build("""{"data":{}}""");

        var consumed = await client.GetFuelConsumedAsync(
            "AFQ9644",
            new DateTime(2026, 9, 18, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 19, 22, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.NotNull(consumed);
        Assert.Null(consumed!.FuelConsumedLitres);
    }

    [Fact]
    public async Task The_fleet_sensor_block_says_which_fuel_sensor_is_fitted()
    {
        // Captured: GET /vehicles. AFQ9644 carries an analog fuel sensor and no CAN read; the
        // other three carry neither, which is what lets the rollup skip their fuel calls.
        const string body = """
        {"data":[{"vehicle_id":32173439,"registration":"AFQ9644",
          "sensors":{"fuel_canbus_consumed":false,"fuel_canbus_level":false,
                     "fuel_analog_level":true,"electric_battery":false,"electric_charging":false}},
         {"vehicle_id":458248840,"registration":"ACQ3455",
          "sensors":{"fuel_canbus_consumed":false,"fuel_canbus_level":false,
                     "fuel_analog_level":false,"electric_battery":false,"electric_charging":false}}]}
        """;

        var (client, _) = Build(body);

        var fleet = await client.GetVehiclesAsync(CancellationToken.None);

        Assert.Equal(2, fleet.Count);
        Assert.True(fleet[0].Sensors?.FuelAnalogLevel);
        Assert.False(fleet[1].Sensors?.FuelAnalogLevel);
    }

    [Fact]
    public async Task The_yard_name_is_read_from_the_field_the_server_actually_uses()
    {
        // Captured: GET /vehicles?limit=1, trimmed. The spec calls this "client_vehicle_name";
        // the server sends "vehicle_name", and the spec's name binds to nothing — which showed up
        // as every vehicle in the fleet looking unnamed in the registration picker.
        const string body = """
        {"data":[{"vehicle_id":32173439,"terminal_serial":"EK087566","registration":"AFQ9644",
          "vehicle_name":"306_AFQ9644","client_vehicle_description":null,
          "manufacturer":"Mercedes-Benz","model":"Axor","model_year":2013,"colour":"White",
          "chassis_number":"WDB9505372L818949","is_under_maintenance":false,
          "terminal_in_repair":false,
          "sensors":{"fuel_canbus_consumed":false,"fuel_canbus_level":false,
                     "fuel_analog_level":true,"electric_battery":false,"electric_charging":false}}]}
        """;

        var (client, _) = Build(body);

        var vehicle = Assert.Single(await client.GetVehiclesAsync(CancellationToken.None));

        Assert.Equal("306_AFQ9644", vehicle.ClientVehicleName);
        Assert.Equal("Mercedes-Benz", vehicle.Manufacturer);
        Assert.Equal("EK087566", vehicle.TerminalSerial);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
    }
}
