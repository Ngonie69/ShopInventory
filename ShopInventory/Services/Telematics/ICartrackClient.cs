using ShopInventory.Models.Telematics;

namespace ShopInventory.Services.Telematics;

/// <summary>
/// Raised when Cartrack throttle a request and ask for longer than
/// <see cref="Configuration.CartrackSettings.MaxRetryAfterSeconds"/>.
/// </summary>
/// <remarks>
/// Deliberately not swallowed into a null result. A caller that cannot tell "throttled" from "no
/// data" writes an empty rollup and marks the day built, and the van reads as having sat still.
/// The jobs catch this, leave the checkpoint where it is and stop cleanly.
/// </remarks>
public sealed class CartrackRateLimitedException(TimeSpan retryAfter, string endpoint)
    : Exception($"Cartrack throttled {endpoint} and asked for {retryAfter.TotalSeconds:0} seconds.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;

    public string Endpoint { get; } = endpoint;
}

/// <summary>
/// The Cartrack Fleet API, one method per call.
/// </summary>
/// <remarks>
/// <para>
/// Every list method returns a flat list: pagination is the client's business, not the caller's.
/// The API's default page size is <b>10</b>, so a caller that forgot to page would quietly see
/// the first ten events of a day and report the rest of the round as never having happened.
/// </para>
/// <para>
/// All reads. The batch fuel endpoints are POSTs limited to 10 requests a minute against the
/// per-registration GETs' 1,000, so the GETs are used even for the whole fleet.
/// </para>
/// </remarks>
public interface ICartrackClient
{
    /// <summary>The fleet, with each vehicle's fitted sensors and workshop state.</summary>
    Task<IReadOnlyList<CartrackVehicle>> GetVehiclesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Driving and idle time per vehicle for one date, as Cartrack bucket it.
    /// </summary>
    /// <remarks>
    /// Cartrack choose the day boundary here, and whether their boundary is a CAT calendar day is
    /// unverified. Treat the durations as usable and the ignition times as provisional — the
    /// authoritative ones come from <see cref="GetEventsAsync"/> over a window we state ourselves.
    /// </remarks>
    Task<IReadOnlyList<CartrackVehicleActivity>> GetActivityAsync(
        DateTime tradingDate, CancellationToken cancellationToken);

    /// <summary>
    /// Terminal events over a window of at most 24 hours — which is exactly one CAT trading day.
    /// </summary>
    Task<IReadOnlyList<CartrackVehicleEvent>> GetEventsAsync(
        DateTime fromUtc, DateTime toUtc, string? registration, CancellationToken cancellationToken);

    /// <summary>Temperature readings over a window of at most 24 hours. Retained ~2 months by Cartrack.</summary>
    Task<IReadOnlyList<CartrackTemperatureReading>> GetTemperaturesAsync(
        DateTime fromUtc, DateTime toUtc, string? registration, CancellationToken cancellationToken);

    /// <summary>The odometer at each end of a window, and the distance between.</summary>
    Task<CartrackOdometerSummary?> GetOdometerAsync(
        string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);

    /// <summary>Litres burned over a window, as the engine reported them. Needs a CAN read.</summary>
    Task<CartrackFuelConsumed?> GetFuelConsumedAsync(
        string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);

    /// <summary>Tank level at each end of a window. Needs a calibrated fuel sensor.</summary>
    Task<CartrackFuelLevel?> GetFuelLevelAsync(
        string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);

    /// <summary>Fill events, with litres, odometer and where each happened.</summary>
    Task<IReadOnlyList<CartrackFuelFill>> GetFuelFillsAsync(
        string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);

    /// <summary>
    /// The whole fleet's live position and telematics in one call — including all four
    /// temperature channels, so a live view needs nothing else.
    /// </summary>
    Task<IReadOnlyList<CartrackVehicleStatus>> GetStatusAsync(CancellationToken cancellationToken);
}
