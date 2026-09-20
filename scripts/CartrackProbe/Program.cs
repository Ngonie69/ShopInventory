// Drives the real CartrackClient against the live Cartrack Fleet API and prints what each
// vehicle can actually tell us.
//
// READ-ONLY BY CONSTRUCTION. Every call this makes is a GET. The Fleet API has write routes —
// immobilise, central locking, alert creation — and none of them are reachable through
// ICartrackClient, so there is nothing here that can change the state of a vehicle.
//
// This exists because four questions cannot be answered from the API documentation, and the rest
// of the integration is sized against their answers:
//
//   1. Which of temp1..temp4 is the load box, per vehicle. Cartrack publish a sensor capability
//      block and it says nothing at all about temperature — a reading can only be found by
//      looking for one.
//   2. Whether the fuel sensors are calibrated on this fleet. If is_calibrated is permanently
//      false, the fuel figures are decoration and the report should say so rather than print them.
//   3. Whether /vehicles/activity buckets a day the way CAT does. Cartrack choose that boundary,
//      not us, and if their day is not our day then every first-ignition time is attached to the
//      wrong rep-day.
//   4. The real shape of the responses. An OpenAPI document describes a server; it is not one.
//
// Usage, from the repo root:
//
//   dotnet run --project scripts/CartrackProbe -- AFQ9644
//   dotnet run --project scripts/CartrackProbe -- AFQ9644 2026-09-18
//
// Credentials come from the API project's user secrets (Cartrack:Username / Cartrack:Password),
// or from CARTRACK_USERNAME / CARTRACK_PASSWORD if you would rather not store them.

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Services;
using ShopInventory.Services.Telematics;

var registration = args.Length > 0 ? args[0] : null;

var probeDate = args.Length > 1 && DateTime.TryParseExact(
    args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
    ? parsed
    : AuditService.ToCAT(DateTime.UtcNow).Date.AddDays(-1);

var configuration = new ConfigurationBuilder()
    .AddUserSecrets(typeof(CartrackSettings).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();

var settings = new CartrackSettings
{
    Enabled = true,
    BaseUrl = configuration["Cartrack:BaseUrl"]
              ?? Environment.GetEnvironmentVariable("CARTRACK_BASEURL")
              ?? "https://fleetapi-zw.cartrack.com/rest",
    Username = configuration["Cartrack:Username"]
               ?? Environment.GetEnvironmentVariable("CARTRACK_USERNAME")
               ?? string.Empty,
    Password = configuration["Cartrack:Password"]
               ?? Environment.GetEnvironmentVariable("CARTRACK_PASSWORD")
               ?? string.Empty,
    TimeoutSeconds = 60
};

if (!settings.HasCredentials)
{
    Console.Error.WriteLine(
        "No Cartrack credentials. Set them in the API project's user secrets:\n" +
        "  cd ShopInventory && dotnet user-secrets set \"Cartrack:Username\" \"...\"\n" +
        "  cd ShopInventory && dotnet user-secrets set \"Cartrack:Password\" \"...\"\n" +
        "or export CARTRACK_USERNAME and CARTRACK_PASSWORD.\n" +
        "Generate them in Fleetweb: API Settings -> Generate User Credentials.");

    return 2;
}

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(options => options.SingleLine = true)
    .SetMinimumLevel(LogLevel.Warning));

var options = Options.Create(settings);
var http = new HttpClient { BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/") };
http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
http.DefaultRequestHeaders.Add("Accept", "application/json");
http.DefaultRequestHeaders.Authorization = CartrackAuthentication.HeaderFor(settings);

var client = new CartrackClient(
    http, options, new CartrackRateLimiter(options), loggerFactory.CreateLogger<CartrackClient>());

Console.WriteLine($"Cartrack probe  ·  {settings.BaseUrl}  ·  trading date {probeDate:yyyy-MM-dd} (CAT)");
Console.WriteLine(new string('=', 100));

// ── 1. The fleet, and what each tracker is fitted with ──────────────────────────────────────

var vehicles = await client.GetVehiclesAsync(CancellationToken.None);

if (registration is not null)
{
    // Matched exactly on the returned field, never on the filter: Cartrack's
    // filter[registration] is a case-insensitive PARTIAL match, so asking for one plate can
    // return several.
    vehicles = vehicles
        .Where(v => TelematicsRegistrationMatches(v.Registration, registration))
        .ToList();
}

Console.WriteLine($"\nFLEET  ({vehicles.Count} vehicle(s))\n");
Console.WriteLine($"  {"Registration",-14} {"Fleet name",-20} {"CAN fuel",-10} {"Analog fuel",-12} {"State",-22}");
Console.WriteLine($"  {new string('-', 14)} {new string('-', 20)} {new string('-', 10)} {new string('-', 12)} {new string('-', 22)}");

foreach (var vehicle in vehicles)
{
    var sensors = vehicle.Sensors;
    var canFuel = (sensors?.FuelCanbusConsumed ?? false) || (sensors?.FuelCanbusLevel ?? false);
    var state = (vehicle.IsUnderMaintenance ?? false) ? "under maintenance"
        : (vehicle.TerminalInRepair ?? false) ? "terminal in repair"
        : "in service";

    Console.WriteLine(
        $"  {vehicle.Registration,-14} {Trim(vehicle.ClientVehicleName, 20),-20} " +
        $"{YesNo(canFuel),-10} {YesNo(sensors?.FuelAnalogLevel),-12} {state,-22}");
}

if (vehicles.Count == 0)
{
    Console.WriteLine("  (none — check the registration, or that this account holds the vehicle)");
    return 1;
}

// ── 2. The day's window, and whether Cartrack agree about where a day starts ────────────────

var (fromUtc, toUtc) = CartrackTime.UtcWindowOf(probeDate);

Console.WriteLine($"\nWINDOW  {fromUtc:yyyy-MM-dd HH:mm}Z .. {toUtc:yyyy-MM-dd HH:mm}Z");
Console.WriteLine($"  sent to the API as  start_timestamp={CartrackTime.ToRequestString(fromUtc)}"
                  + $"  end_timestamp={CartrackTime.ToRequestString(toUtc)}");

var activity = await client.GetActivityAsync(probeDate, CancellationToken.None);

foreach (var vehicle in vehicles)
{
    var plate = vehicle.Registration ?? "(no registration)";

    Console.WriteLine($"\n{new string('=', 100)}\n{plate}   {vehicle.ClientVehicleName}\n");

    // ── Question 3: is Cartrack's day our day? ──────────────────────────────────────────────
    var theirs = activity.FirstOrDefault(a => TelematicsRegistrationMatches(a.Registration, plate));
    var events = await client.GetEventsAsync(fromUtc, toUtc, plate, CancellationToken.None);

    var ours = events
        .Where(e => TelematicsRegistrationMatches(e.Registration, plate))
        .Where(e => string.Equals(e.EventDescription, "IGNITION_ON", StringComparison.OrdinalIgnoreCase))
        .Select(e => CartrackTime.ToUtc(e.EventTs))
        .Where(t => t is not null)
        .OrderBy(t => t)
        .FirstOrDefault();

    var theirsUtc = CartrackTime.ToUtc(theirs?.FirstIgnitionOn);

    Console.WriteLine("  DAY BOUNDARY");
    Console.WriteLine($"    /vehicles/activity first ignition : {Cat(theirsUtc)}   (raw: {theirs?.FirstIgnitionOn ?? "—"})");
    Console.WriteLine($"    our own events window            : {Cat(ours)}");
    Console.WriteLine($"    agree                            : {AgreementOf(theirsUtc, ours)}");
    Console.WriteLine($"    events in the window             : {events.Count}");
    Console.WriteLine($"    driving / idle                   : {Minutes(theirs?.DrivingSeconds)} / {Minutes(theirs?.IdleSeconds)}");

    // ── Question 1: which temperature channel, if any, is fitted ────────────────────────────
    var temperatures = await client.GetTemperaturesAsync(fromUtc, toUtc, plate, CancellationToken.None);
    var mine = temperatures.Where(t => TelematicsRegistrationMatches(t.Registration, plate)).ToList();

    Console.WriteLine("\n  TEMPERATURE   (no capability flag exists — this is the only way to know)");
    Console.WriteLine($"    samples: {mine.Count}");

    foreach (var (channel, values) in new[]
             {
                 (1, mine.Select(t => t.Temp1)),
                 (2, mine.Select(t => t.Temp2)),
                 (3, mine.Select(t => t.Temp3)),
                 (4, mine.Select(t => t.Temp4))
             })
    {
        var reported = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();

        Console.WriteLine(reported.Count == 0
            ? $"    temp{channel}: not fitted / never reported"
            : $"    temp{channel}: {reported.Count,5} readings   min {reported.Min(),7:0.0} °C   "
              + $"max {reported.Max(),7:0.0} °C   avg {reported.Average(),7:0.0} °C");
    }

    // ── Odometer ────────────────────────────────────────────────────────────────────────────
    var odometer = await client.GetOdometerAsync(plate, fromUtc, toUtc, CancellationToken.None);

    Console.WriteLine("\n  ODOMETER");
    Console.WriteLine($"    distance      : {Km(odometer?.DistanceMetres)}");
    Console.WriteLine($"    start / end   : {Km(odometer?.StartOdometerMetres)} / {Km(odometer?.EndOdometerMetres)}");
    Console.WriteLine($"    reset flag    : {YesNo(odometer?.OdometerReset)}      terminal changed: {YesNo(odometer?.TerminalHasChanged)}");
    Console.WriteLine($"    latest event  : {Cat(CartrackTime.ToUtc(odometer?.LatestEventTs))}");

    // ── Question 2: is the fuel sensor calibrated on this fleet? ────────────────────────────
    var consumed = await client.GetFuelConsumedAsync(plate, fromUtc, toUtc, CancellationToken.None);
    var level = await client.GetFuelLevelAsync(plate, fromUtc, toUtc, CancellationToken.None);
    var fills = await client.GetFuelFillsAsync(plate, fromUtc, toUtc, CancellationToken.None);

    Console.WriteLine("\n  FUEL");
    Console.WriteLine($"    consumed      : {Litres(consumed?.FuelConsumedLitres)}");
    Console.WriteLine($"    calibrated    : {YesNo(level?.IsCalibrated)}   "
                      + $"(false here means every fuel figure below is decoration)");
    Console.WriteLine($"    level         : {Litres(level?.Start?.Litres)} -> {Litres(level?.End?.Litres)}   "
                      + $"estimated used {Litres(level?.EstimatedFuelUsedLitres)}");
    Console.WriteLine($"    readings accurate: start {YesNo(level?.Start?.IsAccurate)}  end {YesNo(level?.End?.IsAccurate)}");
    Console.WriteLine($"    fills         : {fills.Count}"
                      + (fills.Count == 0 ? "" : $"  totalling {Litres(fills.Sum(f => f.Litres ?? 0))}"));

    foreach (var fill in fills)
    {
        Console.WriteLine($"      {Cat(CartrackTime.ToUtc(fill.EventTs))}  {Litres(fill.Litres),10}  {fill.Location}");
    }
}

// ── 4. Live status, which is also the live strip's only call ────────────────────────────────

var status = await client.GetStatusAsync(CancellationToken.None);

Console.WriteLine($"\n{new string('=', 100)}\nLIVE STATUS  ({status.Count} vehicle(s) reporting)\n");

foreach (var row in status.Where(s =>
             registration is null || TelematicsRegistrationMatches(s.Registration, registration)))
{
    Console.WriteLine($"  {row.Registration,-14} {Cat(CartrackTime.ToUtc(row.EventTs)),-22} "
                      + $"ign {YesNo(row.Ignition),-5} {row.SpeedKph,4} km/h  "
                      + $"temp1 {row.Temp1?.ToString("0.0") ?? "—",6}  {Trim(row.PositionDescription, 40)}");
}

Console.WriteLine();
return 0;

// ── Formatting ──────────────────────────────────────────────────────────────────────────────

static bool TelematicsRegistrationMatches(string? left, string? right) =>
    ShopInventory.Common.Telematics.TelematicsRegistration.AreSame(left, right);

static string YesNo(bool? value) => value is null ? "—" : value.Value ? "yes" : "no";

static string Trim(string? value, int width) =>
    string.IsNullOrWhiteSpace(value) ? "—"
    : value.Length <= width ? value
    : value[..(width - 1)] + "…";

static string Cat(DateTime? utc) =>
    utc is null ? "—" : AuditService.ToCAT(utc.Value).ToString("yyyy-MM-dd HH:mm:ss") + " CAT";

static string Minutes(int? seconds) =>
    seconds is null ? "—" : $"{seconds.Value / 60} min";

static string Km(long? metres) =>
    metres is null ? "—" : $"{metres.Value / 1000.0:0.0} km";

static string Litres(decimal? litres) =>
    litres is null ? "—" : $"{litres.Value:0.0} L";

static string AgreementOf(DateTime? theirs, DateTime? ours)
{
    if (theirs is null && ours is null) return "both silent — no ignition in this window";
    if (theirs is null) return "ONLY our window has one — their day is not this day";
    if (ours is null) return "ONLY theirs has one — their day reaches outside our window";

    var gap = (theirs.Value - ours.Value).Duration();

    return gap < TimeSpan.FromSeconds(30)
        ? "yes — same ignition, so their day bucket matches CAT"
        : $"NO — {gap.TotalMinutes:0} minutes apart. Do not trust /vehicles/activity's times.";
}
