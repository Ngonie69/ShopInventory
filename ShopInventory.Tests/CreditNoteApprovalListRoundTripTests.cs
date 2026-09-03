using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What one load of <c>/credit-notes/approvals</c> actually costs in SAP round trips, driven through
/// the real <see cref="SAPServiceLayerClient"/> against a Service Layer that answers slowly.
/// </summary>
/// <remarks>
/// The page's whole latency is SAP round trips — nothing here is mirrored locally — so the number of
/// calls and how many of them run at once *is* the performance. A wall-clock assertion would be
/// flaky; the deterministic measure is the depth of the critical path: how many calls the handler
/// waits for one after another. Every call here sleeps the same amount, so a path of four is four
/// times the latency of a path of one.
///
/// In the client's collection because the SAP session lives in statics; see
/// <see cref="SapServiceLayerClientCollection"/>.
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class CreditNoteApprovalListRoundTripTests
{
    /// <summary>Long enough that overlapping calls are unambiguous, short enough to stay a unit test.</summary>
    private static readonly TimeSpan SapLatency = TimeSpan.FromMilliseconds(60);

    [Fact]
    public async Task A_full_page_of_rows_costs_one_wave_of_reads_rather_than_a_chain()
    {
        // A realistic page: 25 requests over 25 drafts, all raised by one person on one template and
        // stage — which is what the live queue looks like, and what makes the labels cacheable.
        var sap = FakeSap.WithPage(rows: 25, total: 5199);
        var handler = CreateHandler(sap);

        await handler.Handle(new GetCreditNoteApprovalsQuery("open", 1, 25), CancellationToken.None);
        var cold = sap.Describe();

        // The labels are read once each and then cached, so a second page pays only for the rows.
        sap.Reset();
        await handler.Handle(new GetCreditNoteApprovalsQuery("open", 2, 25), CancellationToken.None);
        var warm = sap.Describe();

        // Three waves at worst: the rows and their count together, then the drafts, then the labels.
        // It was eight, one behind another, and every one of them is a SAP round trip the manager
        // waits through.
        Assert.True(cold.CriticalPathDepth <= 3, $"cold: {cold}");

        // Warm — every load but the first of the ten-minute window — is the rows and the drafts.
        Assert.True(warm.CriticalPathDepth <= 2, $"warm: {warm}");

        // SAP:MaxConcurrentRequests is 6 for the whole process, of which 2 are reserved for work a
        // person is waiting on. One list may not hold the pool shut against everybody else.
        Assert.True(cold.PeakConcurrency <= 3, $"cold: {cold}");
        Assert.True(warm.PeakConcurrency <= 3, $"warm: {warm}");
    }

    [Fact]
    public async Task The_row_labels_are_read_once_for_the_whole_page()
    {
        var sap = FakeSap.WithPage(rows: 25, total: 5199);
        var handler = CreateHandler(sap);

        await handler.Handle(new GetCreditNoteApprovalsQuery("open", 1, 25), CancellationToken.None);

        // One originator, one template, one stage across 25 rows: three label reads, not seventy-five.
        Assert.Equal(1, sap.CountOf("/ApprovalTemplates("));
        Assert.Equal(1, sap.CountOf("/ApprovalStages("));
        Assert.Equal(1, sap.CountOf("/Users("));
    }

    [Fact]
    public async Task The_drafts_behind_one_page_are_read_in_one_wave()
    {
        var sap = FakeSap.WithPage(rows: 25, total: 5199);
        var handler = CreateHandler(sap);

        await handler.Handle(new GetCreditNoteApprovalsQuery("open", 1, 25), CancellationToken.None);

        var draftReads = sap.Recorded.Where(call => call.Path.EndsWith("/Drafts", StringComparison.Ordinal)).ToList();
        Assert.True(draftReads.Count >= 1);

        // Chunked or not, the draft reads must not queue behind one another.
        var latest = draftReads.Min(call => call.Finished);
        var earliest = draftReads.Max(call => call.Started);
        Assert.True(
            draftReads.Count == 1 || earliest < latest,
            $"The {draftReads.Count} draft reads ran one after another.\n{sap.Describe()}");
    }

    private static GetCreditNoteApprovalsHandler CreateHandler(FakeSap sap)
    {
        var settings = new SAPSettings
        {
            Enabled = true,
            Username = "manager",
            ServiceLayerUrl = "https://sap.invalid/b1s/v1/"
        };

        var client = CreateClient(sap, settings);
        var lookups = new SapApprovalLookups(client, new MemoryCache(new MemoryCacheOptions()), Options.Create(settings));

        return new GetCreditNoteApprovalsHandler(
            client, lookups, Options.Create(settings), NullLogger<GetCreditNoteApprovalsHandler>.Instance);
    }

    private static SAPServiceLayerClient CreateClient(FakeSap sap, SAPSettings settings)
    {
        var httpClient = new HttpClient(sap) { BaseAddress = new Uri(settings.ServiceLayerUrl) };
        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(settings),
            new StubHost(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            new StubUomStore());
    }

    private sealed record Call(string Path, string Query, DateTime Started, DateTime Finished);

    private sealed record Report(int Calls, int CriticalPathDepth, int PeakConcurrency, string Detail)
    {
        public override string ToString() =>
            $"{Calls} call(s), critical path {CriticalPathDepth}, peak concurrency {PeakConcurrency}\n{Detail}";
    }

    /// <summary>
    /// A Service Layer that answers the approval reads after a fixed delay and records when each call
    /// started and finished, so a test can say what ran at the same time as what.
    /// </summary>
    private sealed class FakeSap : HttpMessageHandler
    {
        private readonly List<Call> _calls = [];
        private readonly Lock _gate = new();
        private string _listBody = """{"value":[]}""";
        private string _countBody = "0";
        private string _draftsBody = """{"value":[]}""";

        public IReadOnlyList<Call> Recorded
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToList();
                }
            }
        }

        public static FakeSap WithPage(int rows, int total)
        {
            var requests = Enumerable.Range(0, rows).Select(index => $$"""
                {"Code":{{85000 - index}},"ObjectType":"14","Status":"arsPending","DraftEntry":{{53900 - index}},
                 "CurrentStage":4,"OriginatorID":12,"ApprovalTemplatesID":7,"CreationDate":"2026-09-03"}
                """);

            var drafts = Enumerable.Range(0, rows).Select(index => $$"""
                {"DocEntry":{{53900 - index}},"DocNum":{{53900 - index}},"DocObjectCode":"14","CardCode":"TMP092",
                 "CardName":"Pick n Pay Westgate USD","DocTotal":150.15,"DocCurrency":"USD","DocumentStatus":"bost_Open",
                 "Cancelled":"tNO","AuthorizationStatus":"dasApproved","DocDate":"2026-09-03"}
                """);

            return new FakeSap
            {
                _listBody = $$"""{"value":[{{string.Join(",", requests)}}]}""",
                _countBody = total.ToString(),
                _draftsBody = $$"""{"value":[{{string.Join(",", drafts)}}]}"""
            };
        }

        public void Reset()
        {
            lock (_gate)
            {
                _calls.Clear();
            }
        }

        public int CountOf(string pathFragment) =>
            Recorded.Count(call => call.Path.Contains(pathFragment, StringComparison.Ordinal));

        /// <summary>
        /// How many calls deep the longest wait-on-a-wait chain is: a call that started only after an
        /// earlier one finished sits one level below it. This is the latency the page pays.
        /// </summary>
        public Report Describe()
        {
            var calls = Recorded.OrderBy(call => call.Started).ToList();
            var depth = new int[calls.Count];

            for (var index = 0; index < calls.Count; index++)
            {
                depth[index] = 1;
                for (var earlier = 0; earlier < index; earlier++)
                {
                    if (calls[earlier].Finished <= calls[index].Started)
                    {
                        depth[index] = Math.Max(depth[index], depth[earlier] + 1);
                    }
                }
            }

            var peak = calls.Count == 0
                ? 0
                : calls.Max(call => calls.Count(other => other.Started < call.Finished && other.Finished > call.Started));

            var detail = string.Join(
                "\n",
                calls.Select((call, index) =>
                    $"  [{depth[index]}] {call.Path}{Shorten(call.Query)}"));

            return new Report(calls.Count, depth.Length == 0 ? 0 : depth.Max(), peak, detail);
        }

        private static string Shorten(string query) =>
            query.Length <= 90 ? query : query[..90] + "…";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Json("""{"SessionId":"test-session"}""");
            }

            if (path.EndsWith("/Logout", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var started = DateTime.UtcNow;
            await Task.Delay(SapLatency, cancellationToken);

            lock (_gate)
            {
                _calls.Add(new Call(path, request.RequestUri.Query, started, DateTime.UtcNow));
            }

            if (path.EndsWith("/ApprovalRequests/$count", StringComparison.Ordinal))
            {
                return Text(_countBody);
            }

            if (path.EndsWith("/ApprovalRequests", StringComparison.Ordinal))
            {
                return Json(_listBody);
            }

            if (path.EndsWith("/Drafts", StringComparison.Ordinal))
            {
                return Json(_draftsBody);
            }

            if (path.Contains("/ApprovalTemplates(", StringComparison.Ordinal))
            {
                return Json("""{"Code":7,"Name":"Factory Credit Notes","IsActive":"tYES"}""");
            }

            if (path.Contains("/ApprovalStages(", StringComparison.Ordinal))
            {
                return Json("""
                    {"Code":4,"Name":"Production WashBay","NoOfApproversRequired":1,
                     "ApprovalStageApprovers":[{"UserID":1}]}
                    """);
            }

            if (path.Contains("/Users(", StringComparison.Ordinal))
            {
                return Json("""{"InternalKey":12,"UserCode":"Inv","UserName":"Rose"}""");
            }

            if (path.EndsWith("/Users", StringComparison.Ordinal))
            {
                return Json("""{"value":[{"InternalKey":1,"UserCode":"manager","UserName":"Site Manager"}]}""");
            }

            return Json("""{"error":{"message":{"value":"no route"}}}""", HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Text(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubUomStore : ISapItemUomMappingStore
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
