using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Covers what the fiscalisation client does when no API key is installed.
/// </summary>
/// <remarks>
/// The platform refuses every keyless request with a 401, so sending one only ever bought the same
/// answer more slowly. On 2026-08-15 production ran all day without a key and wrote 133 warnings, each
/// with a stack trace, from receipt lookups that could not have succeeded — after startup had already
/// said so once. The client now refuses locally, and the lookup that produced those warnings says
/// nothing above Debug for that case.
/// </remarks>
public class FiscalisationApiKeyGuardTests
{
    // ── The client refuses before sending ───────────────────────────────

    [Fact]
    public async Task AReceiptCheckWithoutAKeyIsRefusedWithoutBeingSent()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var client = Client(handler, configuredKey: null);

        var ex = await Assert.ThrowsAsync<FiscalisationApiException>(
            () => client.CheckReceiptAsync(0, "755765", ReceiptType.FiscalInvoice));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal(FiscalisationApiClient.ApiKeyNotConfiguredErrorCode, ex.ErrorCode);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task ASubmissionWithoutAKeyIsRefusedWithoutBeingSent()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var client = Client(handler, configuredKey: null);

        var ex = await Assert.ThrowsAsync<FiscalisationApiException>(
            () => client.SubmitSapReceiptAsync(new SapFiscaliseReceiptApiRequest()));

        Assert.Equal(FiscalisationApiClient.ApiKeyNotConfiguredErrorCode, ex.ErrorCode);
        Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankKeyCountsAsNoKey(string? configuredKey)
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var client = Client(handler, configuredKey);

        await Assert.ThrowsAsync<FiscalisationApiException>(() => client.GetFiscalStatusAsync(deviceId: 1));

        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task AKeyThatIsInstalledStillGoesOut()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"isFiscalised":false,"matches":[]}""");
        var client = Client(handler, configuredKey: "installed-key");

        var response = await client.CheckReceiptAsync(0, "755765", ReceiptType.FiscalInvoice);

        Assert.False(response.IsFiscalised);
        Assert.Equal(["installed-key"], handler.ApiKeys);
    }

    [Fact]
    public async Task TheSettingsProbeStillSendsACandidateKeyWhenNoneIsInstalled()
    {
        // This is exactly the situation the settings screen exists for: nothing installed yet, an
        // administrator typing the first key in. The guard must not stand in the way of that.
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var client = Client(handler, configuredKey: null);

        await client.GetFiscalConfigWithApiKeyAsync("candidate-key", deviceId: 0);

        Assert.Equal(["candidate-key"], handler.ApiKeys);
    }

    [Fact]
    public async Task TheSettingsProbeWithNoCandidateAndNoInstalledKeyIsRefused()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var client = Client(handler, configuredKey: null);

        await Assert.ThrowsAsync<FiscalisationApiException>(
            () => client.GetFiscalConfigWithApiKeyAsync("   ", deviceId: 0));

        Assert.Empty(handler.Paths);
    }

    // ── What the receipt lookup logs ────────────────────────────────────

    [Fact]
    public async Task ALookupWithoutAKeyReturnsNullAndSaysNothingAboveDebug()
    {
        var log = new CapturingLogger();
        var client = Client(new CapturingHandler(HttpStatusCode.OK, "{}"), configuredKey: null);

        var snapshot = await FiscalReceiptLookup.TryLookupAsync(
            client, new NoConfigCache(), 755765, ReceiptType.FiscalInvoice, log, CancellationToken.None);

        // Null, not "not fiscalised": we could not find out, and the two must never be confused.
        Assert.Null(snapshot);
        Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("no Fiscalisation API key"));
    }

    [Fact]
    public async Task ALookupThePlatformRefusesWarnsOnceWithoutAStackTrace()
    {
        var log = new CapturingLogger();
        var client = Client(
            new CapturingHandler(HttpStatusCode.Unauthorized, """{"errorCode":"RejectedByApiKey","detail":"Invalid or missing X-API-Key header"}"""),
            configuredKey: "stale-key");

        var snapshot = await FiscalReceiptLookup.TryLookupAsync(
            client, new NoConfigCache(), 755765, ReceiptType.FiscalInvoice, log, CancellationToken.None);

        Assert.Null(snapshot);
        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        // The platform's own explanation is in the line; the HttpClient stack underneath it is not.
        Assert.Contains("401", warning.Message);
        Assert.Contains("RejectedByApiKey", warning.Message);
        Assert.Contains("Invalid or missing X-API-Key header", warning.Message);
        Assert.Null(warning.Exception);
    }

    [Fact]
    public async Task ALookupThatCannotReachThePlatformStillCarriesTheException()
    {
        var log = new CapturingLogger();
        var client = Client(new CapturingHandler(new HttpRequestException("connection refused")), configuredKey: "installed-key");

        var snapshot = await FiscalReceiptLookup.TryLookupAsync(
            client, new NoConfigCache(), 755765, ReceiptType.FiscalInvoice, log, CancellationToken.None);

        Assert.Null(snapshot);
        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        // A transport failure is the case where the exception itself is the useful part.
        Assert.IsType<HttpRequestException>(warning.Exception);
    }

    // ── Harness ─────────────────────────────────────────────────────────

    private static FiscalisationApiClient Client(CapturingHandler handler, string? configuredKey)
    {
        var settings = new FiscalisationSettings
        {
            BaseUrl = "https://fiscal.example/",
            ApiKey = configuredKey ?? string.Empty,
            DefaultDeviceId = 1,
            TransientRetryCount = 0
        };

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(settings.BaseUrl) };

        // Exactly what Program.cs does: the default header exists only when a key does.
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            httpClient.DefaultRequestHeaders.Add("X-API-Key", configuredKey);
        }

        return new FiscalisationApiClient(
            httpClient,
            Options.Create(settings),
            NullLogger<FiscalisationApiClient>.Instance);
    }

    private sealed class NoConfigCache : IFiscalDeviceConfigCache
    {
        public Task<FiscalConfigApiResponse?> TryGetAsync(int deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult<FiscalConfigApiResponse?>(null);
    }

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

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
