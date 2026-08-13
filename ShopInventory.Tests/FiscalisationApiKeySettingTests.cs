using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.FiscalisationConfiguration;
using ShopInventory.Features.FiscalisationConfiguration.Commands.UpdateFiscalisationSettings;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Covers entering the Fiscalisation API key from Settings.
/// </summary>
/// <remarks>
/// The failure modes worth guarding are all quiet ones: a key handed back out through the screen it
/// was typed into, a probe that tests the old key instead of the new one, a platform outage read as a
/// bad key, and a key saved with a line break in it that only surfaces at the next app pool recycle,
/// as a fiscalisation client that will not construct.
/// </remarks>
public class FiscalisationApiKeySettingTests
{
    // ── The mask ────────────────────────────────────────────────────────

    [Fact]
    public void MaskRevealsOnlyTheTailOfTheKey()
    {
        const string key = "fsk_live_9d41c0f7b3a24e6f";

        var masked = FiscalisationApiKeyMask.Mask(key);

        Assert.NotNull(masked);
        Assert.EndsWith("4e6f", masked);
        Assert.DoesNotContain("fsk_live", masked);
        // Not the length either — that is a hint about the key.
        Assert.NotEqual(key.Length, masked.Length);
    }

    [Fact]
    public void MaskHidesAShortKeyOutright()
    {
        // Four characters of an eight-character key is half of it.
        var masked = FiscalisationApiKeyMask.Mask("abc12345");

        Assert.Equal("••••••••", masked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MaskIsNullWhenNoKeyIsConfigured(string? key)
        => Assert.Null(FiscalisationApiKeyMask.Mask(key));

    // ── Probing with a key that is not the configured one ───────────────

    [Fact]
    public async Task ASuppliedKeyIsTheOneSentToThePlatform()
    {
        // The whole point of the settings screen: verify what was typed, not what is installed.
        var handler = new CapturingHandler(HttpStatusCode.OK, FiscalConfigJson);
        var client = Client(handler, configuredKey: "installed-key");

        await client.GetFiscalConfigWithApiKeyAsync("candidate-key", deviceId: 3);

        Assert.Equal(["candidate-key"], handler.ApiKeys);
        Assert.Contains("deviceId=3", handler.Paths.Single());
    }

    [Fact]
    public async Task TheInstalledKeyIsUsedWhenNoneIsSupplied()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, FiscalConfigJson);
        var client = Client(handler, configuredKey: "installed-key");

        await client.GetFiscalConfigWithApiKeyAsync("   ", deviceId: 0);

        Assert.Equal(["installed-key"], handler.ApiKeys);
    }

    // ── Reading without pinning a device ────────────────────────────────

    /// <summary>
    /// An unpinned deployment must name no device at all. <c>deviceId=0</c> is a supplied value to the
    /// platform, so it skips its own fallback and refuses the read outright — which is how a good key
    /// came back unverifiable on a deployment that had never set a device id.
    /// </summary>
    [Fact]
    public async Task AnUnpinnedConfigReadNamesNoDeviceOnTheWire()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, FiscalConfigJson);

        await Client(handler, "installed-key").GetFiscalConfigWithApiKeyAsync("candidate-key", deviceId: 0);

        Assert.DoesNotContain("deviceId", handler.Paths.Single());
    }

    [Fact]
    public async Task AnUnpinnedStatusReadNamesNoDeviceOnTheWire()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, FiscalStatusJson);

        await Client(handler, "installed-key").GetFiscalStatusAsync(deviceId: 0);

        Assert.DoesNotContain("deviceId", handler.Paths.Single());
    }

    [Fact]
    public async Task APinnedStatusReadStillNamesItsDevice()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, FiscalStatusJson);

        await Client(handler, "installed-key").GetFiscalStatusAsync(deviceId: 7);

        Assert.Contains("deviceId=7", handler.Paths.Single());
    }

    [Fact]
    public async Task AnUnpinnedProbeReportsTheDeviceThePlatformChose()
    {
        var probe = await Probe(new CapturingHandler(HttpStatusCode.OK, FiscalConfigJson), deviceId: 0);

        Assert.Equal(FiscalisationApiKeyVerdict.Accepted, probe.Verdict);
        // Not "device 0", which names a device nobody chose and no console has.
        Assert.DoesNotContain("device 0", probe.Message);
        Assert.Contains("ZIM-0001", probe.Message);
    }

    // ── What each platform answer means for the key ─────────────────────

    [Fact]
    public async Task AConfigReadMeansTheKeyWorks()
    {
        var probe = await Probe(new CapturingHandler(HttpStatusCode.OK, FiscalConfigJson));

        Assert.Equal(FiscalisationApiKeyVerdict.Accepted, probe.Verdict);
        Assert.Contains("KEFALOS CHEESE", probe.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task OnlyThePlatformRefusingTheKeyCountsAsARejection(HttpStatusCode status)
    {
        var probe = await Probe(new CapturingHandler(status, """{"errorCode":"RejectedByApiKey","detail":"no"}"""));

        Assert.Equal(FiscalisationApiKeyVerdict.Rejected, probe.Verdict);
    }

    [Theory]
    // The device does not exist, or the platform is broken — either way the key got past the door.
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AFailureBeyondTheKeyCheckIsInconclusive(HttpStatusCode status)
    {
        var probe = await Probe(new CapturingHandler(status, """{"errorCode":"DeviceNotFound","detail":"no such device"}"""));

        Assert.Equal(FiscalisationApiKeyVerdict.Inconclusive, probe.Verdict);
    }

    [Fact]
    public async Task AnUnreachablePlatformIsInconclusiveRatherThanABadKey()
    {
        var probe = await Probe(new CapturingHandler(new HttpRequestException("connection refused")));

        Assert.Equal(FiscalisationApiKeyVerdict.Inconclusive, probe.Verdict);
    }

    // ── Saving ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    public async Task ABlankKeyIsRefused(string key)
    {
        var result = await Save(key, testConnection: false, new CapturingHandler(HttpStatusCode.OK, FiscalConfigJson));

        Assert.True(result.IsError);
        Assert.Equal("FiscalisationSettings.ApiKeyRequired", result.FirstError.Code);
    }

    [Theory]
    // A key that wrapped on the way here. Stored as-is it throws inside HttpClient's header validation
    // at the next startup and takes the whole fiscalisation client with it.
    [InlineData("fsk_live_9d41\nc0f7b3a24e6f")]
    [InlineData("fsk_live_9d41 c0f7b3a24e6f")]
    [InlineData("fsk_live_9d41\tc0f7b3a24e6f")]
    public async Task AKeyBrokenAcrossLinesIsRefusedRatherThanStored(string key)
    {
        var result = await Save(key, testConnection: false, new CapturingHandler(HttpStatusCode.OK, FiscalConfigJson));

        Assert.True(result.IsError);
        Assert.Equal("FiscalisationSettings.UpdateFailed", result.FirstError.Code);
    }

    [Fact]
    public async Task AKeyThePlatformRefusesIsNotStored()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized, """{"errorCode":"RejectedByApiKey","detail":"no"}""");

        var result = await Save("candidate-key", testConnection: true, handler);

        Assert.True(result.IsError);
        Assert.Equal("FiscalisationSettings.ApiKeyRejected", result.FirstError.Code);
        Assert.Contains("was not saved", result.FirstError.Description);
        // And it was the typed key that was checked, not the installed one.
        Assert.Equal(["candidate-key"], handler.ApiKeys);
    }

    [Fact]
    public async Task AnUnreachablePlatformDoesNotBlockTheSave()
    {
        // An administrator installing a key during an outage is the case this protects: the platform
        // being down says nothing about the key, and refusing the save would lock them out of the fix.
        var (result, writtenConfig) = await SaveAgainstARestoredWebConfig(
            "candidate-key", new CapturingHandler(new HttpRequestException("down")));

        Assert.False(result.IsError);
        // Unknown, not failed — the screen paints this amber rather than green or red.
        Assert.Null(result.Value.ConnectionTestPassed);
        Assert.Contains("Could not reach", result.Value.Message);
        Assert.Contains(@"name=""Fiscalisation__ApiKey"" value=""candidate-key""", writtenConfig);
    }

    [Fact]
    public async Task AVerifiedKeyIsStoredAndReportedAsVerified()
    {
        var (result, writtenConfig) = await SaveAgainstARestoredWebConfig(
            "candidate-key", new CapturingHandler(HttpStatusCode.OK, FiscalConfigJson));

        Assert.False(result.IsError);
        Assert.True(result.Value.ConnectionTestPassed);
        // Only the mask comes back out, never the key.
        Assert.Equal("••••••••-key", result.Value.ApiKeyMasked);
        Assert.Contains(@"name=""Fiscalisation__ApiKey"" value=""candidate-key""", writtenConfig);
    }

    // ── Harness ─────────────────────────────────────────────────────────

    private const string FiscalConfigJson = """
        {
          "operationID": "op-1",
          "taxPayerName": "KEFALOS CHEESE PRODUCTS",
          "taxPayerTIN": "1000000000",
          "deviceSerialNo": "ZIM-0001",
          "deviceBranchName": "Harare",
          "deviceOperatingMode": "Online",
          "qrUrl": "https://receipt.zimra.org/"
        }
        """;

    private const string FiscalStatusJson = """
        {
          "deviceId": 36189,
          "fiscalDayNo": 12,
          "fiscalDayStatus": "FiscalDayOpened"
        }
        """;

    private static FiscalisationSettings Settings(string configuredKey) => new()
    {
        BaseUrl = "https://fiscal.example/",
        ApiKey = configuredKey,
        DefaultDeviceId = 1,
        TransientRetryCount = 0
    };

    private static FiscalisationApiClient Client(CapturingHandler handler, string configuredKey)
    {
        var settings = Settings(configuredKey);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(settings.BaseUrl) };

        // Exactly what Program.cs does, because the override under test is the request header beating
        // this default one.
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            httpClient.DefaultRequestHeaders.Add("X-API-Key", configuredKey);
        }

        return new FiscalisationApiClient(
            httpClient,
            Options.Create(settings),
            NullLogger<FiscalisationApiClient>.Instance);
    }

    private static Task<FiscalisationApiKeyProbeResult> Probe(CapturingHandler handler, int deviceId = 1)
        => FiscalisationApiKeyProbe.RunAsync(
            Client(handler, "installed-key"),
            "candidate-key",
            deviceId,
            NullLogger.Instance,
            CancellationToken.None);

    /// <summary>
    /// Runs a save against the API's web.config, which the build copies next to the tests, and puts the
    /// file back afterwards.
    /// </summary>
    /// <remarks>
    /// The XML write is the half of the handler nothing else here would catch going wrong — a node
    /// appended outside the environmentVariables element, or a value that does not survive escaping —
    /// and it only ever runs where that file exists.
    /// </remarks>
    private static async Task<(ErrorOr.ErrorOr<UpdateFiscalisationSettingsResult> Result, string WrittenConfig)>
        SaveAgainstARestoredWebConfig(string apiKey, CapturingHandler handler)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "web.config");
        Assert.True(File.Exists(path), $"Expected the API's web.config to be copied to {path} by the build.");

        var original = await File.ReadAllBytesAsync(path);
        try
        {
            var result = await Save(apiKey, testConnection: true, handler);
            return (result, await File.ReadAllTextAsync(path));
        }
        finally
        {
            await File.WriteAllBytesAsync(path, original);
        }
    }

    private static Task<ErrorOr.ErrorOr<UpdateFiscalisationSettingsResult>> Save(
        string apiKey, bool testConnection, CapturingHandler handler)
    {
        var handlerUnderTest = new UpdateFiscalisationSettingsHandler(
            Client(handler, "installed-key"),
            new StaticOptionsMonitor(Settings("installed-key")),
            new NoOpAuditService(),
            NullLogger<UpdateFiscalisationSettingsHandler>.Instance);

        return handlerUnderTest.Handle(
            new UpdateFiscalisationSettingsCommand(
                new UpdateFiscalisationSettingsRequest { ApiKey = apiKey, TestConnection = testConnection },
                "tester"),
            CancellationToken.None);
    }

    /// <summary>Records the API key each request actually carried, and answers however it was told to.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _failure;

        public CapturingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public CapturingHandler(Exception failure)
        {
            _failure = failure;
            _body = string.Empty;
        }

        public List<string?> ApiKeys { get; } = [];

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKeys.Add(request.Headers.TryGetValues("X-API-Key", out var values) ? values.Single() : null);
            Paths.Add(request.RequestUri!.PathAndQuery);

            if (_failure is not null)
            {
                throw _failure;
            }

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StaticOptionsMonitor(FiscalisationSettings value) : IOptionsMonitor<FiscalisationSettings>
    {
        public FiscalisationSettings CurrentValue => value;

        public FiscalisationSettings Get(string? name) => value;

        public IDisposable? OnChange(Action<FiscalisationSettings, string?> listener) => null;
    }

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) => Task.CompletedTask;
    }
}
