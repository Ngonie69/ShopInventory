using System.Net;
using System.Text;
using System.Text.Json;
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
/// <see cref="SAPServiceLayerClient"/> keeps its session in statics, so these must not run beside
/// each other or beside anything else that drives the client.
/// </summary>
[CollectionDefinition("SapServiceLayerClient", DisableParallelization = true)]
public sealed class SapServiceLayerClientCollection;

/// <summary>
/// Covers the existence probe in <c>EnsureSqlQueryAsync</c>. Every SQL-backed call used to pay a
/// <c>GET SQLQueries('code')</c> before its execute, which on the common single-page query doubled
/// the SAP round-trips. Skipping it has to stay safe when the statement behind a fixed code
/// changes, and has to give way when SAP stops behaving as though the query is what we recorded.
/// </summary>
[Collection("SapServiceLayerClient")]
public class SapSqlQueryProvisioningTests
{
    private const string Sql = "SELECT \"DocEntry\" FROM ORDR";
    private const string OtherSql = "SELECT \"DocEntry\", \"DocNum\" FROM ORDR";

    [Fact]
    public async Task Repeat_call_with_the_same_statement_does_not_probe_again()
    {
        var sap = new FakeServiceLayer();
        var client = CreateClient(sap);

        await client.ExecuteRawSqlQueryAsync("SHOP_TEST", "Test query", Sql);
        await client.ExecuteRawSqlQueryAsync("SHOP_TEST", "Test query", Sql);

        Assert.Equal(1, sap.ProbeCount);
        Assert.Equal(1, sap.CreateCount);
        Assert.Equal(2, sap.ExecuteCount);
    }

    [Fact]
    public async Task Changing_the_statement_behind_a_fixed_code_probes_and_repairs_it()
    {
        // SHOP_-prefixed codes are fixed, not content-addressed, so a release that edits the SQL
        // reuses the code. Trusting the memo on the code alone would run the old statement.
        var sap = new FakeServiceLayer();
        var client = CreateClient(sap);

        await client.ExecuteRawSqlQueryAsync("SHOP_TEST", "Test query", Sql);
        await client.ExecuteRawSqlQueryAsync("SHOP_TEST", "Test query", OtherSql);

        Assert.Equal(2, sap.ProbeCount);
        Assert.Equal(1, sap.PatchCount);
        Assert.Equal(OtherSql, sap.StoredSql["SHOP_TEST"]);
    }

    [Fact]
    public async Task A_failed_execution_makes_the_next_call_probe_again()
    {
        var sap = new FakeServiceLayer();
        var client = CreateClient(sap);

        await client.ExecuteRawSqlQueryAsync("SHOP_TEST", "Test query", Sql);
        Assert.Equal(1, sap.ProbeCount);

        sap.FailNextExecute = true;
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.ExecuteRawSqlQueryAsync("SHOP_TEST", "Test query", Sql));

        await client.ExecuteRawSqlQueryAsync("SHOP_TEST", "Test query", Sql);

        Assert.Equal(2, sap.ProbeCount);
    }

    [Fact]
    public async Task Distinct_statements_are_each_verified_once()
    {
        var sap = new FakeServiceLayer();
        var client = CreateClient(sap);

        await client.ExecuteScopedRawSqlQueryAsync("RPT", "Report", Sql);
        await client.ExecuteScopedRawSqlQueryAsync("RPT", "Report", OtherSql);
        await client.ExecuteScopedRawSqlQueryAsync("RPT", "Report", Sql);
        await client.ExecuteScopedRawSqlQueryAsync("RPT", "Report", OtherSql);

        // Content-addressed codes differ, so the two statements never contend for one entry.
        Assert.Equal(2, sap.ProbeCount);
        Assert.Equal(2, sap.CreateCount);
        Assert.Equal(0, sap.PatchCount);
        Assert.Equal(4, sap.ExecuteCount);
    }

    private static SAPServiceLayerClient CreateClient(FakeServiceLayer sap)
    {
        var httpClient = new HttpClient(sap)
        {
            BaseAddress = new Uri("https://sap.invalid/b1s/v1/")
        };

        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings { ServiceLayerUrl = "https://sap.invalid/b1s/v1/" }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            new StubItemUomMappingStore());
    }

    /// <summary>
    /// Stands in for the Service Layer's SQLQueries endpoint, storing statements by code the way
    /// SAP does so the probe / create / patch decisions are exercised for real.
    /// </summary>
    private sealed class FakeServiceLayer : HttpMessageHandler
    {
        public Dictionary<string, string> StoredSql { get; } = new(StringComparer.Ordinal);
        public int ProbeCount { get; private set; }
        public int CreateCount { get; private set; }
        public int PatchCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public bool FailNextExecute { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;

            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Json("{\"SessionId\":\"test-session\"}");
            }

            if (path.EndsWith("/SQLQueries", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                CreateCount++;
                var payload = JsonDocument.Parse(await ReadBodyAsync(request, cancellationToken));
                StoredSql[payload.RootElement.GetProperty("SqlCode").GetString()!] =
                    payload.RootElement.GetProperty("SqlText").GetString()!;
                return Json("{}", HttpStatusCode.Created);
            }

            var code = ExtractCode(path);

            if (path.EndsWith("/List", StringComparison.Ordinal))
            {
                ExecuteCount++;
                if (FailNextExecute)
                {
                    FailNextExecute = false;
                    return Json("{\"error\":{\"message\":\"boom\"}}", HttpStatusCode.InternalServerError);
                }

                return Json("{\"value\":[]}");
            }

            if (request.Method == HttpMethod.Patch)
            {
                PatchCount++;
                var payload = JsonDocument.Parse(await ReadBodyAsync(request, cancellationToken));
                StoredSql[code] = payload.RootElement.GetProperty("SqlText").GetString()!;
                return Json("{}", HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Get && query.Contains("SqlText", StringComparison.Ordinal))
            {
                ProbeCount++;
                return StoredSql.TryGetValue(code, out var stored)
                    ? Json(JsonSerializer.Serialize(new { SqlText = stored }))
                    : Json("{}", HttpStatusCode.NotFound);
            }

            return Json("{}", HttpStatusCode.NotFound);
        }

        /// <summary>Pulls <c>CODE</c> out of a <c>SQLQueries('CODE')</c> or <c>…/List</c> path.</summary>
        private static string ExtractCode(string path)
        {
            var open = path.IndexOf('\'');
            var close = path.IndexOf('\'', open + 1);
            return open < 0 || close < 0 ? string.Empty : path[(open + 1)..close];
        }

        private static async Task<string> ReadBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
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

    private sealed class StubItemUomMappingStore : ISapItemUomMappingStore
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
