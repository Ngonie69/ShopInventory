using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Signing in to the Fiscalisation platform for the fiscal-day and offline-file routes.
/// </summary>
/// <remarks>
/// These routes sit behind the platform's bearer token rather than the integration's API key, and the
/// failure modes are all quiet ones: a client that re-authenticates per call and fills the taxpayer's audit
/// trail with its own sign-ins, a token cached past a revocation so every call 401s until a restart, and a
/// 403 that reads like a wrong password when it is really the account holding the Admin role.
/// </remarks>
public sealed class FiscalDayAdminApiClientTests
{
    [Fact]
    public async Task The_token_is_obtained_once_and_reused()
    {
        var handler = new ScriptedHandler();
        var client = Client(handler);

        await client.GenerateOfflineFileAsync(new GenerateOfflineFileApiRequest { DeviceId = 1, FiscalDayNo = 2 });
        await client.GenerateOfflineFileAsync(new GenerateOfflineFileApiRequest { DeviceId = 1, FiscalDayNo = 3 });

        // Every sign-in is an audited event on the platform, so one per call would be this integration
        // writing the taxpayer's audit trail full of itself.
        Assert.Equal(1, handler.TokenRequests);
        Assert.Equal(["Bearer token-1", "Bearer token-1"], handler.Authorizations);
    }

    [Fact]
    public async Task A_refused_token_is_replaced_and_the_call_repeated()
    {
        // A token can stop being accepted before its stated expiry — a password change or a revoked session
        // does exactly that — and the cached expiry says nothing about it.
        var handler = new ScriptedHandler { RefuseFirstCall = true };
        var client = Client(handler);

        await client.GenerateOfflineFileAsync(new GenerateOfflineFileApiRequest { DeviceId = 1, FiscalDayNo = 2 });

        Assert.Equal(2, handler.TokenRequests);
        Assert.Equal(["Bearer token-1", "Bearer token-2"], handler.Authorizations);
    }

    /// <summary>
    /// Repeating on a 401 is safe only because the platform's authorization runs before the endpoint, so a
    /// 401 proves the handler never ran. Anything else may have reached FDMS, and an offline file uploaded
    /// twice cannot be withdrawn.
    /// </summary>
    [Fact]
    public async Task A_refusal_that_is_not_a_401_is_never_repeated()
    {
        var handler = new ScriptedHandler
        {
            CallStatus = HttpStatusCode.Conflict,
            CallBody = """{"errorCode":"FdmsOperationIndeterminate","detail":"FDMS did not answer."}"""
        };

        var failure = await Assert.ThrowsAsync<FiscalisationApiException>(() =>
            Client(handler).SubmitOfflineFileAsync(new SubmitOfflineFileApiRequest { DeviceId = 1, FileJson = "{}" }));

        Assert.True(failure.RequiresReconciliation);
        Assert.Single(handler.Authorizations);
    }

    [Fact]
    public async Task An_administrator_account_is_named_as_the_cause_rather_than_a_bad_password()
    {
        var handler = new ScriptedHandler
        {
            TokenStatus = HttpStatusCode.Forbidden,
            TokenBody = """{"error":"Administrator password tokens are disabled."}"""
        };

        var failure = await Assert.ThrowsAsync<FiscalisationApiException>(() =>
            Client(handler).CloseFiscalDayAsync(new CloseDayApiRequest { DeviceId = 1 }));

        Assert.Contains("Administrator password tokens are disabled", failure.Message);
        // The two reasons the platform answers 403 here, both of which look like a wrong password and are not.
        Assert.Contains("Admin role", failure.Message);
        Assert.Contains("one-time password", failure.Message);
    }

    /// <summary>
    /// The platform's device authorisation reads a single <c>FdmsDeviceId</c> claim off the token, so one
    /// account is authorised for exactly one device. Signing every call with one credential means N-1
    /// devices in a fleet collect a bodyless 403 on everything.
    /// </summary>
    [Fact]
    public async Task Each_device_signs_in_with_its_own_service_account()
    {
        var handler = new ScriptedHandler();
        var client = Client(
            handler,
            username: "fallback",
            deviceAccounts: new Dictionary<string, FiscalDayServiceAccountSettings>
            {
                ["36189"] = new() { Username = "svc-36189", Password = "pw-1" },
                ["36190"] = new() { Username = "svc-36190", Password = "pw-2" }
            });

        await client.CloseFiscalDayAsync(new CloseDayApiRequest { DeviceId = 36189 });
        await client.CloseFiscalDayAsync(new CloseDayApiRequest { DeviceId = 36190 });
        // The same device again reuses its own cached token rather than signing in a third time.
        await client.CloseFiscalDayAsync(new CloseDayApiRequest { DeviceId = 36189 });

        Assert.Equal(["svc-36189", "svc-36190"], handler.TokenUsernames);
        Assert.Equal(["Bearer token-1", "Bearer token-2", "Bearer token-1"], handler.Authorizations);
    }

    /// <summary>
    /// One device, one account: the default is the whole configuration for a single-device deployment and
    /// the fallback for any device without an entry of its own.
    /// </summary>
    [Fact]
    public async Task A_device_with_no_entry_of_its_own_falls_back_to_the_default_account()
    {
        var handler = new ScriptedHandler();
        var client = Client(
            handler,
            username: "fallback",
            deviceAccounts: new Dictionary<string, FiscalDayServiceAccountSettings>
            {
                ["36189"] = new() { Username = "svc-36189", Password = "pw-1" }
            });

        await client.CloseFiscalDayAsync(new CloseDayApiRequest { DeviceId = 99 });

        Assert.Equal(["fallback"], handler.TokenUsernames);
    }

    /// <summary>
    /// The platform answers this 403 with no body at all, so without help the only thing recorded against
    /// the day is the word "Forbidden" — which reads as a wrong password and is almost never one.
    /// </summary>
    [Fact]
    public async Task A_bodyless_403_names_the_device_the_account_and_what_to_configure()
    {
        var handler = new ScriptedHandler { CallStatus = HttpStatusCode.Forbidden, CallBody = string.Empty };

        var failure = await Assert.ThrowsAsync<FiscalisationApiException>(() =>
            Client(handler, username: "fiscal-service")
                .SubmitOfflineFileAsync(new SubmitOfflineFileApiRequest { DeviceId = 36189, FileJson = "{}" }));

        Assert.Contains("36189", failure.Message);
        Assert.Contains("fiscal-service", failure.Message);
        Assert.Contains("FdmsDeviceId", failure.Message);
        Assert.Contains("DeviceServiceAccounts__36189", failure.Message);
        // The permissions the role has to carry, so the fix does not need the platform's source.
        Assert.Contains("receipt-submit", failure.Message);
    }

    [Fact]
    public async Task Nothing_is_sent_when_no_service_account_is_configured()
    {
        var handler = new ScriptedHandler();

        var failure = await Assert.ThrowsAsync<FiscalisationApiException>(() =>
            Client(handler, username: "", password: "")
                .GenerateOfflineFileAsync(new GenerateOfflineFileApiRequest { DeviceId = 1, FiscalDayNo = 2 }));

        Assert.Equal(FiscalDayAdminApiClient.ServiceAccountNotConfiguredErrorCode, failure.ErrorCode);
        Assert.Equal(0, handler.TokenRequests);
        Assert.Empty(handler.Authorizations);
    }

    [Fact]
    public async Task The_file_status_read_names_the_device_and_both_bounds()
    {
        var handler = new ScriptedHandler { CallBody = """{"total":0,"fileStatus":[]}""" };

        await Client(handler).GetOfflineFileStatusAsync(
            deviceId: 36189,
            fileUploadedFrom: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Unspecified),
            fileUploadedTill: new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Unspecified));

        var path = Assert.Single(handler.Paths);
        Assert.Contains("deviceId=36189", path);
        // The platform refuses the read without both bounds, and binds them as unqualified local times.
        Assert.Contains("2026-08-01T00%3A00%3A00", path);
        Assert.Contains("2026-08-19T00%3A00%3A00", path);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static FiscalDayAdminApiClient Client(
        ScriptedHandler handler,
        string username = "fiscal-service",
        string password = "not-a-real-password",
        Dictionary<string, FiscalDayServiceAccountSettings>? deviceAccounts = null)
    {
        var settings = new FiscalisationSettings
        {
            BaseUrl = "https://fiscal.example/",
            FiscalDay = new FiscalDaySettings
            {
                ServiceAccount = new FiscalDayServiceAccountSettings
                {
                    Username = username,
                    Password = password
                },
                DeviceServiceAccounts = deviceAccounts ?? []
            }
        };

        return new FiscalDayAdminApiClient(
            new HttpClient(handler) { BaseAddress = new Uri(settings.BaseUrl) },
            new FiscalDayServiceAccountTokenStore(),
            Options.Create(settings),
            NullLogger<FiscalDayAdminApiClient>.Instance);
    }

    /// <summary>
    /// Answers the token route with a fresh token each time and every other route with whatever the test
    /// installed, recording the Authorization header each call actually carried.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public int TokenRequests { get; private set; }

        /// <summary>Which account each sign-in was made with, in order.</summary>
        public List<string?> TokenUsernames { get; } = [];

        public List<string?> Authorizations { get; } = [];

        public List<string> Paths { get; } = [];

        public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

        public string? TokenBody { get; set; }

        public HttpStatusCode CallStatus { get; set; } = HttpStatusCode.OK;

        public string CallBody { get; set; } = "{}";

        /// <summary>Refuses the first non-token call, as a revoked token would.</summary>
        public bool RefuseFirstCall { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;

            if (path.Contains("auth/token", StringComparison.Ordinal))
            {
                TokenRequests++;
                TokenUsernames.Add(await ReadUsernameAsync(request, cancellationToken));

                var body = TokenBody ?? $$"""
                    {
                      "accessToken": "token-{{TokenRequests}}",
                      "tokenType": "Bearer",
                      "expiresAt": "{{DateTime.UtcNow.AddHours(1):yyyy-MM-ddTHH:mm:ss}}"
                    }
                    """;

                return Respond(TokenStatus, body);
            }

            Paths.Add(path);
            Authorizations.Add(request.Headers.Authorization?.ToString());

            if (RefuseFirstCall)
            {
                RefuseFirstCall = false;
                return Respond(HttpStatusCode.Unauthorized, string.Empty);
            }

            return Respond(CallStatus, CallBody);
        }

        private static async Task<string?> ReadUsernameAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("username", out var username)
                ? username.GetString()
                : null;
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string body)
            => new(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
    }
}
