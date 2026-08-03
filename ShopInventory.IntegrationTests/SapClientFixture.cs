using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Services;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// One <see cref="SAPServiceLayerClient"/> against the configured Service Layer, shared by the
/// whole run.
/// </summary>
/// <remarks>
/// Shared because the client holds its SAP session in statics: a fixture per class would still
/// share the session but would multiply the logins for no benefit.
/// </remarks>
public sealed class SapClientFixture : IDisposable
{
    private readonly HttpClient? _httpClient;

    public SapClientFixture()
    {
        if (SapAvailability.SkipReason is not null)
        {
            return;
        }

        var settings = SapAvailability.Settings;

        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                // Test instances are commonly fronted by a self-signed certificate. This is a
                // developer-run integration harness pointed at a host the developer chose, never
                // the application's own trust decision.
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        })
        {
            BaseAddress = new Uri(settings.ServiceLayerUrl),
            // Service Layer latency here is heavy-tailed, and a SQLQueries write costs tens of
            // seconds on its own. At two minutes a cold report query timed out on one run and
            // answered in seconds on the next, which reads as a broken change rather than a busy
            // server — the ambiguity this harness is meant to remove.
            Timeout = TimeSpan.FromMinutes(5)
        };

        var services = new ServiceCollection().BuildServiceProvider();

        Client = new SAPServiceLayerClient(
            _httpClient,
            new SingleClientFactory(_httpClient),
            Options.Create(settings),
            new IntegrationHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            new NullItemUomMappingStore());
    }

    public ISAPServiceLayerClient Client { get; } = null!;

    public void Dispose() => _httpClient?.Dispose();

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class IntegrationHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShopInventory.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>Keeps UoM resolution off the local database; nothing here needs it persisted.</summary>
    private sealed class NullItemUomMappingStore : ISapItemUomMappingStore
    {
        public Task<IReadOnlyDictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>> GetAsync(
            IReadOnlyCollection<SapItemUomKey> keys,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>>(
                new Dictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>());

        public Task SaveAsync(
            IReadOnlyCollection<(SapItemUomKey Key, string? UoMCode, int UoMEntry)> mappings,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

[CollectionDefinition("SAP")]
public sealed class SapCollection : ICollectionFixture<SapClientFixture>;
