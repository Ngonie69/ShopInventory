using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Services.Telematics;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The Cartrack client against a stubbed transport — everything that can be proved without
/// credentials.
/// </summary>
/// <remarks>
/// Three of these guard failures that would be silent in production rather than loud: a page size
/// left unsent returns the API's default of ten rows and reads as a short day; a throttle
/// swallowed into an empty list reads as a van that never moved; and a Basic credential encoded
/// the wrong way is a bare 401 on a request whose Authorization header is deliberately never
/// logged.
/// </remarks>
public class CartrackClientTests
{
    private const string Vehicles = """{"data":[{"registration":"AFQ9644"}],"meta":{"current_page":1,"last_page":1,"per_page":500,"total":1}}""";

    private static CartrackSettings Settings() => new()
    {
        Enabled = true,
        BaseUrl = "https://fleetapi-zw.cartrack.com/rest",
        Username = "api_user",
        Password = "s3cret",
        MaxRetries = 2,
        RetryDelaysMs = [1, 1, 1],
        PageSize = 500,
        MaxPages = 5,
        MaxRetryAfterSeconds = 30
    };

    private static (CartrackClient Client, RecordingHandler Handler) Build(
        CartrackSettings settings, params HttpResponseMessage[] responses)
    {
        var handler = new RecordingHandler(responses);

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/")
        };

        http.DefaultRequestHeaders.Authorization = CartrackAuthentication.HeaderFor(settings);

        var client = new CartrackClient(
            http,
            Options.Create(settings),
            new CartrackRateLimiter(Options.Create(settings)),
            NullLogger<CartrackClient>.Instance);

        return (client, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // — Authentication ————————————————————————————————————————————————

    [Fact]
    public void The_credential_is_basic_utf8_of_username_colon_password()
    {
        var header = CartrackAuthentication.HeaderFor(new CartrackSettings
        {
            Username = "api_user",
            Password = "s3cret"
        });

        Assert.NotNull(header);
        Assert.Equal("Basic", header!.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("api_user:s3cret")),
            header.Parameter);
    }

    [Fact]
    public void A_password_outside_ascii_is_encoded_as_utf8_not_latin1()
    {
        // The whole reason the encoding is stated explicitly. Latin-1 would produce a different
        // credential and a bare 401 with nothing to look at.
        var header = CartrackAuthentication.HeaderFor(new CartrackSettings
        {
            Username = "api_user",
            Password = "påssword"
        });

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header!.Parameter!));

        Assert.Equal("api_user:påssword", decoded);
    }

    [Fact]
    public void A_password_keeps_its_trailing_space_but_the_username_does_not()
    {
        // A generated secret may legitimately end in whitespace; a username typed into a field
        // may accidentally.
        var header = CartrackAuthentication.HeaderFor(new CartrackSettings
        {
            Username = "  api_user  ",
            Password = "s3cret "
        });

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header!.Parameter!));

        Assert.Equal("api_user:s3cret ", decoded);
    }

    [Theory]
    [InlineData("", "s3cret")]
    [InlineData("api_user", "")]
    [InlineData("", "")]
    public void A_half_credential_produces_no_header_at_all(string username, string password)
    {
        var header = CartrackAuthentication.HeaderFor(new CartrackSettings
        {
            Username = username,
            Password = password
        });

        Assert.Null(header);
    }

    [Fact]
    public async Task Every_request_carries_the_credential()
    {
        var (client, handler) = Build(Settings(), Json(Vehicles));

        await client.GetVehiclesAsync(CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("Basic", sent.Headers.Authorization?.Scheme);
    }

    // — Paging ————————————————————————————————————————————————————————

    [Fact]
    public async Task A_page_size_is_always_sent_because_the_api_default_is_ten()
    {
        var (client, handler) = Build(Settings(), Json(Vehicles));

        await client.GetVehiclesAsync(CancellationToken.None);

        var query = handler.Requests[0].RequestUri!.Query;

        Assert.Contains("limit=500", query);
        Assert.Contains("page=1", query);
    }

    [Fact]
    public async Task Paging_walks_to_the_last_page_and_stops()
    {
        var settings = Settings();

        var (client, handler) = Build(
            settings,
            Json("""{"data":[{"registration":"A1"}],"meta":{"current_page":1,"last_page":3,"per_page":500,"total":3}}"""),
            Json("""{"data":[{"registration":"A2"}],"meta":{"current_page":2,"last_page":3,"per_page":500,"total":3}}"""),
            Json("""{"data":[{"registration":"A3"}],"meta":{"current_page":3,"last_page":3,"per_page":500,"total":3}}"""));

        var vehicles = await client.GetVehiclesAsync(CancellationToken.None);

        Assert.Equal(3, vehicles.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(["A1", "A2", "A3"], vehicles.Select(v => v.Registration));
    }

    [Fact]
    public async Task Paging_stops_on_a_short_page_when_there_is_no_meta_block()
    {
        // Several of these endpoints answer without pagination metadata at all.
        var (client, handler) = Build(Settings(), Json("""{"data":[{"registration":"A1"}]}"""));

        var vehicles = await client.GetVehiclesAsync(CancellationToken.None);

        Assert.Single(vehicles);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Paging_gives_up_at_the_page_cap_rather_than_looping_forever()
    {
        var settings = Settings();
        settings.MaxPages = 3;

        var never = Enumerable.Range(0, 10)
            .Select(_ => Json("""{"data":[{"registration":"A"}],"meta":{"current_page":1,"last_page":99,"per_page":500,"total":5000}}"""))
            .ToArray();

        var (client, handler) = Build(settings, never);

        var vehicles = await client.GetVehiclesAsync(CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, vehicles.Count);
    }

    // — Throttling ————————————————————————————————————————————————————

    [Fact]
    public async Task A_throttle_within_the_cap_is_waited_out_and_the_call_succeeds()
    {
        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.Add("X-RateLimit-Retry-After-Seconds", "1");

        var (client, handler) = Build(Settings(), throttled, Json(Vehicles));

        var vehicles = await client.GetVehiclesAsync(CancellationToken.None);

        Assert.Single(vehicles);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_throttle_asking_for_longer_than_the_cap_throws_rather_than_parking_the_job()
    {
        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.Add("X-RateLimit-Retry-After-Seconds", "600");

        var (client, _) = Build(Settings(), throttled);

        var thrown = await Assert.ThrowsAsync<CartrackRateLimitedException>(
            () => client.GetVehiclesAsync(CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(600), thrown.RetryAfter);
        Assert.Contains("vehicles", thrown.Endpoint);
    }

    [Fact]
    public async Task A_throttle_is_never_reported_as_an_empty_result()
    {
        // The failure this guards: an empty list is indistinguishable from a van that did not
        // move, and would be written into a rollup as fact.
        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.Add("X-RateLimit-Retry-After-Seconds", "9999");

        var (client, _) = Build(Settings(), throttled);

        await Assert.ThrowsAsync<CartrackRateLimitedException>(
            () => client.GetVehiclesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_standard_retry_after_header_is_honoured_when_cartracks_own_is_absent()
    {
        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.Add("Retry-After", "1");

        var (client, handler) = Build(Settings(), throttled, Json(Vehicles));

        await client.GetVehiclesAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    // — Transient failures ————————————————————————————————————————————

    [Fact]
    public async Task A_server_error_is_retried()
    {
        var (client, handler) = Build(
            Settings(),
            Json("{}", HttpStatusCode.ServiceUnavailable),
            Json(Vehicles));

        var vehicles = await client.GetVehiclesAsync(CancellationToken.None);

        Assert.Single(vehicles);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        // A 404 or a 401 will not become a 200 by asking again, and retrying a bad credential
        // three times is how an account gets locked.
        var (client, handler) = Build(Settings(), Json("{}", HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetVehiclesAsync(CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    // — Reading the body ——————————————————————————————————————————————

    [Fact]
    public async Task The_irregular_field_names_are_read_rather_than_silently_nulled()
    {
        // temp1..temp4 have no separator and "recieved_ts" is misspelled at source, so an
        // automatic snake_case policy maps some fields and nulls these.
        var body = """
        {"data":[{"registration":"AFQ9644","event_ts":"2026-09-18 06:52:03+02",
          "recieved_ts":"2026-09-18 06:52:40+02","temp1":-18.4,"temp2":3.5,
          "temp3":null,"temp4":null}]}
        """;

        var (client, _) = Build(Settings(), Json(body));

        var readings = await client.GetTemperaturesAsync(
            new DateTime(2026, 9, 17, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 18, 22, 0, 0, DateTimeKind.Utc),
            registration: null,
            CancellationToken.None);

        var reading = Assert.Single(readings);
        Assert.Equal(-18.4m, reading.Temp1);
        Assert.Equal(3.5m, reading.Temp2);
        Assert.Null(reading.Temp3);
        Assert.Equal("2026-09-18 06:52:40+02", reading.ReceivedTs);
    }

    [Fact]
    public async Task A_window_is_sent_as_the_accounts_own_clock_with_no_offset()
    {
        var (client, handler) = Build(Settings(), Json("""{"data":[]}"""));

        // Midnight CAT on the 18th, which is 22:00Z on the 17th.
        await client.GetEventsAsync(
            new DateTime(2026, 9, 17, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 18, 22, 0, 0, DateTimeKind.Utc),
            registration: null,
            CancellationToken.None);

        var query = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query);

        Assert.Contains("start_timestamp=2026-09-18 00:00:00", query);
        Assert.Contains("end_timestamp=2026-09-19 00:00:00", query);
        Assert.DoesNotContain("+02", query);
    }

    [Fact]
    public async Task An_empty_body_is_no_rows_rather_than_a_crash()
    {
        var (client, _) = Build(Settings(), Json(string.Empty));

        Assert.Empty(await client.GetVehiclesAsync(CancellationToken.None));
    }

    /// <summary>Hands back a queued response per call and keeps every request that was made.</summary>
    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            var response = responses[Math.Min(_next, responses.Length - 1)];
            _next++;
            response.RequestMessage = request;

            return Task.FromResult(response);
        }
    }
}
