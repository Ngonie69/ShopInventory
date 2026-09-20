using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Models.Telematics;

namespace ShopInventory.Services.Telematics;

/// <summary>
/// The Cartrack Fleet API over HTTP, with the retrying, paging and throttling the API requires.
/// </summary>
/// <remarks>
/// <para>
/// Authentication is HTTP Basic, attached once in the <c>AddHttpClient</c> callback in
/// <c>Program.cs</c>. Two things about that are easy to get wrong and expensive to debug: the
/// credential is encoded as <b>UTF-8</b>, because a password containing a non-ASCII character
/// encoded as Latin-1 produces a plain 401 with nothing to distinguish it from a wrong password;
/// and the header is never logged, by this class or anything it calls.
/// </para>
/// <para>
/// Retry follows <c>RevmaxClient</c>, with one addition: a 429 is answered by waiting whatever
/// Cartrack's own headers ask for rather than by the backoff ladder, and by telling the limiter
/// to hold the budget shut so sibling calls do not walk into the same wall.
/// </para>
/// </remarks>
public sealed class CartrackClient : ICartrackClient
{
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes =
    [
        HttpStatusCode.RequestTimeout,      // 408
        HttpStatusCode.TooManyRequests,     // 429
        HttpStatusCode.InternalServerError, // 500
        HttpStatusCode.BadGateway,          // 502
        HttpStatusCode.ServiceUnavailable,  // 503
        HttpStatusCode.GatewayTimeout       // 504
    ];

    private readonly HttpClient _http;
    private readonly CartrackSettings _settings;
    private readonly ICartrackRateLimiter _limiter;
    private readonly ILogger<CartrackClient> _logger;
    private readonly JsonSerializerOptions _json;

    public CartrackClient(
        HttpClient http,
        IOptions<CartrackSettings> settings,
        ICartrackRateLimiter limiter,
        ILogger<CartrackClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _limiter = limiter;
        _logger = logger;

        // Every property is named explicitly on the model. Case-insensitive as a safety net for a
        // field the spec spells differently from the server, but no naming policy: an automatic
        // snake_case mapping would hit temp1..temp4 and the misspelled recieved_ts and null them.
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = true
        };
    }

    public Task<IReadOnlyList<CartrackVehicle>> GetVehiclesAsync(CancellationToken cancellationToken) =>
        GetPagedAsync<CartrackVehicle>("vehicles", [], CartrackBudget.Global, cancellationToken);

    public Task<IReadOnlyList<CartrackVehicleActivity>> GetActivityAsync(
        DateTime tradingDate, CancellationToken cancellationToken) =>
        GetPagedAsync<CartrackVehicleActivity>(
            "vehicles/activity",
            [("filter[date]", CartrackTime.ToDateString(tradingDate))],
            CartrackBudget.Global,
            cancellationToken);

    public Task<IReadOnlyList<CartrackVehicleEvent>> GetEventsAsync(
        DateTime fromUtc, DateTime toUtc, string? registration, CancellationToken cancellationToken) =>
        GetPagedAsync<CartrackVehicleEvent>(
            "vehicles/events",
            Window(fromUtc, toUtc, registration),
            CartrackBudget.Events,
            cancellationToken);

    public Task<IReadOnlyList<CartrackTemperatureReading>> GetTemperaturesAsync(
        DateTime fromUtc, DateTime toUtc, string? registration, CancellationToken cancellationToken) =>
        GetPagedAsync<CartrackTemperatureReading>(
            "topics/vehicles/temperature",
            Window(fromUtc, toUtc, registration),
            CartrackBudget.Global,
            cancellationToken);

    public Task<CartrackOdometerSummary?> GetOdometerAsync(
        string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
        GetSingleAsync<CartrackOdometerSummary>(
            $"vehicles/{Escape(registration)}/odometer",
            Window(fromUtc, toUtc, registration: null),
            cancellationToken);

    public Task<CartrackFuelConsumed?> GetFuelConsumedAsync(
        string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
        GetSingleAsync<CartrackFuelConsumed>(
            $"fuel/consumed/{Escape(registration)}",
            Window(fromUtc, toUtc, registration: null),
            cancellationToken);

    public Task<CartrackFuelLevel?> GetFuelLevelAsync(
        string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
        GetSingleAsync<CartrackFuelLevel>(
            $"fuel/level/{Escape(registration)}",
            Window(fromUtc, toUtc, registration: null),
            cancellationToken);

    public Task<IReadOnlyList<CartrackFuelFill>> GetFuelFillsAsync(
        string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
        GetPagedAsync<CartrackFuelFill>(
            $"fuel/fills/{Escape(registration)}",
            Window(fromUtc, toUtc, registration: null),
            CartrackBudget.Global,
            cancellationToken);

    public Task<IReadOnlyList<CartrackVehicleStatus>> GetStatusAsync(CancellationToken cancellationToken) =>
        GetPagedAsync<CartrackVehicleStatus>(
            "vehicles/status",
            [("odometer_in_km", "false")],
            CartrackBudget.Status,
            cancellationToken);

    // — Plumbing ————————————————————————————————————————————————————————

    private List<(string Key, string Value)> Window(DateTime fromUtc, DateTime toUtc, string? registration)
    {
        var query = new List<(string, string)>
        {
            ("start_timestamp", CartrackTime.ToRequestString(fromUtc)),
            ("end_timestamp", CartrackTime.ToRequestString(toUtc))
        };

        if (!string.IsNullOrWhiteSpace(registration))
        {
            query.Add(("filter[registration]", registration.Trim()));
        }

        return query;
    }

    /// <summary>
    /// Walks every page of a list endpoint.
    /// </summary>
    /// <remarks>
    /// The page cap is a guard against a mis-read envelope, not a limit anybody should hit, so
    /// reaching it is logged as a warning: silently returning the first 200 pages of a day would
    /// be a report that is wrong rather than a report that failed.
    /// </remarks>
    private async Task<IReadOnlyList<T>> GetPagedAsync<T>(
        string path,
        IReadOnlyList<(string Key, string Value)> query,
        string budget,
        CancellationToken cancellationToken)
    {
        var all = new List<T>();
        var page = 1;

        while (page <= Math.Max(1, _settings.MaxPages))
        {
            var paged = query
                .Append(("page", page.ToString(CultureInfo.InvariantCulture)))
                .Append(("limit", Math.Max(1, _settings.PageSize).ToString(CultureInfo.InvariantCulture)))
                .ToList();

            var envelope = await SendAsync<CartrackEnvelope<List<T>>>(path, paged, budget, cancellationToken);

            if (envelope?.Data is { Count: > 0 } rows)
            {
                all.AddRange(rows);
            }

            var meta = envelope?.Meta;

            // Stop on the paging metadata when it is there, and on a short page when it is not —
            // several of these endpoints answer without a meta block at all.
            if (meta is null || meta.LastPage <= 0)
            {
                if (envelope?.Data is null || envelope.Data.Count < _settings.PageSize)
                {
                    return all;
                }
            }
            else if (meta.CurrentPage >= meta.LastPage)
            {
                return all;
            }

            page++;
        }

        _logger.LogWarning(
            "Cartrack {Path} stopped at the {MaxPages}-page cap with {Rows} rows. The result is "
            + "truncated — raise Cartrack:MaxPages or narrow the window.",
            path, _settings.MaxPages, all.Count);

        return all;
    }

    private async Task<T?> GetSingleAsync<T>(
        string path,
        IReadOnlyList<(string Key, string Value)> query,
        CancellationToken cancellationToken)
        where T : class
    {
        var envelope = await SendAsync<CartrackEnvelope<T>>(
            path, query, CartrackBudget.Global, cancellationToken);

        return envelope?.Data;
    }

    private async Task<T?> SendAsync<T>(
        string path,
        IReadOnlyList<(string Key, string Value)> query,
        string budget,
        CancellationToken cancellationToken)
        where T : class
    {
        var url = path + BuildQuery(query);
        var delays = _settings.EffectiveRetryDelaysMs;
        var maxRetries = Math.Max(0, _settings.MaxRetries);

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _limiter.WaitAsync(budget, cancellationToken);

            HttpResponseMessage response;

            try
            {
                response = await _http.GetAsync(url, cancellationToken);
            }
            catch (Exception ex) when (
                (ex is HttpRequestException
                 || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                && attempt < maxRetries)
            {
                var delay = delays[Math.Min(attempt, delays.Length - 1)];

                _logger.LogWarning(ex,
                    "Cartrack {Path} failed to reach the server. Retrying in {Delay}ms "
                    + "(attempt {Attempt}/{MaxRetries}).",
                    path, delay, attempt + 1, maxRetries);

                await Task.Delay(delay, cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = RetryAfterOf(response);

                    // Tell the limiter before deciding what to do, so any sibling call already
                    // queued behind this one waits rather than adding to the violation.
                    _limiter.Pause(budget, retryAfter);

                    if (retryAfter > TimeSpan.FromSeconds(Math.Max(1, _settings.MaxRetryAfterSeconds)))
                    {
                        throw new CartrackRateLimitedException(retryAfter, path);
                    }

                    if (attempt < maxRetries)
                    {
                        _logger.LogWarning(
                            "Cartrack throttled {Path} and asked for {Seconds}s. Waiting "
                            + "(attempt {Attempt}/{MaxRetries}).",
                            path, retryAfter.TotalSeconds, attempt + 1, maxRetries);

                        await Task.Delay(retryAfter, cancellationToken);
                        continue;
                    }
                }
                else if (RetryableStatusCodes.Contains(response.StatusCode) && attempt < maxRetries)
                {
                    var delay = delays[Math.Min(attempt, delays.Length - 1)];

                    _logger.LogWarning(
                        "Cartrack {Path} answered {StatusCode}. Retrying in {Delay}ms "
                        + "(attempt {Attempt}/{MaxRetries}).",
                        path, (int)response.StatusCode, delay, attempt + 1, maxRetries);

                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(content))
                {
                    return null;
                }

                try
                {
                    return JsonSerializer.Deserialize<T>(content, _json);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "Cartrack {Path} returned a body that could not be read: {Content}",
                        path, Truncate(content));

                    throw;
                }
            }
        }
    }

    /// <summary>
    /// How long Cartrack asked us to wait. Their own header first, then the standard one, then
    /// the absolute form; a throttle with no usable header still waits a beat rather than none.
    /// </summary>
    private static TimeSpan RetryAfterOf(HttpResponseMessage response)
    {
        if (TryHeaderSeconds(response, "X-RateLimit-Retry-After-Seconds", out var seconds)
            || TryHeaderSeconds(response, "Retry-After", out seconds))
        {
            return TimeSpan.FromSeconds(Math.Max(1, seconds));
        }

        if (response.Headers.TryGetValues("X-RateLimit-Retry-At", out var at)
            && DateTimeOffset.TryParse(
                at.FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
        {
            var wait = when - DateTimeOffset.UtcNow;

            return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
        }

        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        return TimeSpan.FromSeconds(5);
    }

    private static bool TryHeaderSeconds(HttpResponseMessage response, string name, out int seconds)
    {
        seconds = 0;

        return response.Headers.TryGetValues(name, out var values)
               && int.TryParse(
                   values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
    }

    private static string BuildQuery(IReadOnlyList<(string Key, string Value)> query) =>
        query.Count == 0
            ? string.Empty
            : "?" + string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string Escape(string registration) => Uri.EscapeDataString(registration.Trim());

    private static string Truncate(string content) =>
        content.Length <= 4096 ? content : content[..4096] + "…";
}
