using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// When the SAP client logs in, and what it does to the session it already has.
/// </summary>
/// <remarks>
/// The session used to be renewed 25 minutes after login however busy it was, by logging out the
/// session other requests were still using and logging in again under the process-wide login lock.
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class SapSessionLifetimeTests : IAsyncLifetime
{
    private readonly FakeSap _sap = new();
    private readonly ManualClock _clock = new();
    private readonly SAPServiceLayerClient _client;

    public SapSessionLifetimeTests()
    {
        // The session is static and other test classes set it from the system clock, so this clock
        // starts from the real time rather than its own fixed date.
        _clock.Advance(DateTimeOffset.UtcNow - _clock.GetUtcNow());
        _client = CreateClient(_sap, _clock, loginTimeoutSeconds: 10);
    }

    public async Task InitializeAsync()
    {
        await _client.LogoutAsync();
        _sap.Reset();
    }

    public Task DisposeAsync() => _client.LogoutAsync();

    [Fact]
    public async Task A_session_in_use_is_kept_rather_than_renewed_on_a_timer()
    {
        for (var call = 0; call < 4; call++)
        {
            await _client.GetInvoiceByDocEntryAsync(1);
            _clock.Advance(TimeSpan.FromMinutes(20));
        }

        // Sixty minutes on one session: never idle for longer than twenty.
        Assert.Equal(1, _sap.Logins);
        Assert.Equal(0, _sap.Logouts);
    }

    [Fact]
    public async Task An_idle_session_is_replaced_without_logging_the_old_one_out()
    {
        await _client.GetInvoiceByDocEntryAsync(1);
        _clock.Advance(TimeSpan.FromMinutes(26));
        await _client.GetInvoiceByDocEntryAsync(1);

        Assert.Equal(2, _sap.Logins);
        Assert.Equal(0, _sap.Logouts);
    }

    [Fact]
    public async Task The_idle_timeout_SAP_reports_at_login_is_the_one_used()
    {
        // Ten minutes, less the five-minute margin.
        _sap.SessionTimeoutMinutes = 10;

        await _client.GetInvoiceByDocEntryAsync(1);
        _clock.Advance(TimeSpan.FromMinutes(4));
        await _client.GetInvoiceByDocEntryAsync(1);
        Assert.Equal(1, _sap.Logins);

        _clock.Advance(TimeSpan.FromMinutes(6));
        await _client.GetInvoiceByDocEntryAsync(1);
        Assert.Equal(2, _sap.Logins);
    }

    [Fact]
    public async Task A_login_SAP_never_answers_fails_after_its_own_budget()
    {
        _sap.LoginHangs = true;

        var call = _client.GetInvoiceByDocEntryAsync(1);
        await _sap.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(TimeSpan.FromSeconds(11));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("10 seconds", ex.Message);
    }

    private static SAPServiceLayerClient CreateClient(FakeSap sap, ManualClock clock, int loginTimeoutSeconds)
    {
        var httpClient = new HttpClient(sap)
        {
            BaseAddress = new Uri("https://sap.invalid/b1s/v1/"),
            Timeout = TimeSpan.FromMinutes(5)
        };

        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings
            {
                ServiceLayerUrl = "https://sap.invalid/b1s/v1/",
                LoginTimeoutSeconds = loginTimeoutSeconds
            }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            StubProxy.Unused<ISapItemUomMappingStore>(),
            timeProvider: clock);
    }

    /// <summary>Answers Login with a new session each time, counts Logouts, and 404s every document.</summary>
    private sealed class FakeSap : HttpMessageHandler
    {
        private int _logins;
        private int _logouts;

        public int Logins => Volatile.Read(ref _logins);
        public int Logouts => Volatile.Read(ref _logouts);
        public int SessionTimeoutMinutes { get; set; } = 30;
        public bool LoginHangs { get; set; }
        public TaskCompletionSource LoginStarted { get; private set; } = NewSignal();

        public void Reset()
        {
            Interlocked.Exchange(ref _logins, 0);
            Interlocked.Exchange(ref _logouts, 0);
            LoginStarted = NewSignal();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                var login = Interlocked.Increment(ref _logins);
                LoginStarted.TrySetResult();

                if (LoginHangs)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return Json($"{{\"SessionId\":\"session-{login}\",\"SessionTimeout\":{SessionTimeoutMinutes}}}");
            }

            if (path.EndsWith("/Logout", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _logouts);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return Json("{\"error\":{\"code\":-2028,\"message\":{\"value\":\"No matching records found\"}}}", HttpStatusCode.NotFound);
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
