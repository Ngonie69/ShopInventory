using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

public sealed class PodReportCacheTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public PodReportCacheTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Saved_report_round_trips_as_a_fresh_local_snapshot()
    {
        var store = CreateStore();
        var report = new PodUploadStatusReportDto
        {
            FromDate = "2026-07-01",
            ToDate = "2026-07-28",
            TotalInvoices = 1,
            PendingCount = 1,
            Items =
            [
                new PodUploadStatusItemDto
                {
                    DocEntry = 123,
                    DocNum = 456,
                    CardCode = "C001",
                    CardName = "Cached customer",
                    IsCrateInvoice = true
                }
            ]
        };

        await store.SaveAsync(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 28),
            "global",
            report,
            CancellationToken.None);

        var snapshot = await store.GetAsync(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 28),
            "global",
            CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsFresh);
        var item = Assert.Single(snapshot.Report.Items);
        Assert.Equal(123, item.DocEntry);
        Assert.Equal("Cached customer", item.CardName);
        Assert.True(item.IsCrateInvoice);
    }

    [Fact]
    public async Task Expired_report_is_retained_for_explicit_stale_fallback()
    {
        var cacheKey = PodReportCacheStore.CreateCacheKey(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 28),
            "global");
        _context.PodReportCacheEntries.Add(new PodReportCacheEntryEntity
        {
            CacheKey = cacheKey,
            FromDate = new DateTime(2026, 7, 1),
            ToDate = new DateTime(2026, 7, 28),
            ScopeKey = "global",
            PayloadJson = """{"fromDate":"2026-07-01","toDate":"2026-07-28","items":[]}""",
            RefreshedAtUtc = DateTime.UtcNow.AddHours(-1),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1)
        });
        await _context.SaveChangesAsync();

        var snapshot = await CreateStore().GetAsync(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 28),
            "global",
            CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.False(snapshot.IsFresh);
    }

    [Fact]
    public async Task Fresh_snapshot_serves_the_report_without_calling_sap()
    {
        var store = CreateStore();
        var fromDate = new DateTime(2026, 7, 1);
        var toDate = new DateTime(2026, 7, 28);
        await store.SaveAsync(
            fromDate,
            toDate,
            "global",
            new PodUploadStatusReportDto
            {
                FromDate = "2026-07-01",
                ToDate = "2026-07-28",
                Items =
                [
                    new PodUploadStatusItemDto
                    {
                        DocEntry = 123,
                        DocNum = 456,
                        CardCode = "C001",
                        CardName = "Local report customer"
                    }
                ]
            },
            CancellationToken.None);

        var documentService = StubProxy.For<IDocumentService>((method, _) =>
            method.Name == nameof(IDocumentService.GetPodStatusByDocEntriesAsync)
                ? Task.FromResult(new Dictionary<int, PodStatusInfo>
                {
                    [123] = new()
                    {
                        UploadedAt = new DateTime(2026, 7, 28, 8, 30, 0, DateTimeKind.Utc),
                        UploadedBy = "local-uploader",
                        Count = 2
                    }
                })
                : throw new InvalidOperationException($"IDocumentService.{method.Name} was not expected."));
        var handler = new GetPodUploadStatusHandler(
            StubProxy.Unused<ISAPServiceLayerClient>(),
            documentService,
            _context,
            Options.Create(new SAPSettings { Enabled = false }),
            store,
            NullLogger<GetPodUploadStatusHandler>.Instance);

        var result = await handler.Handle(
            new GetPodUploadStatusQuery(fromDate, toDate, UserId: null),
            CancellationToken.None);

        Assert.False(result.IsError);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal("Local report customer", item.CardName);
        Assert.True(item.HasPod);
        Assert.Equal("local-uploader", item.PodUploadedBy);
        Assert.Equal(2, item.PodCount);
        Assert.Equal(1, result.Value.UploadedCount);
    }

    [Fact]
    public void Driver_scope_is_order_independent_and_changes_with_assignments()
    {
        var first = GetPodUploadStatusHandler.BuildCacheScopeKey(
            includeCreditNoteActivity: false,
            isPodOperator: false,
            [" c002 ", "C001"]);
        var reordered = GetPodUploadStatusHandler.BuildCacheScopeKey(
            includeCreditNoteActivity: false,
            isPodOperator: false,
            ["c001", "c002"]);
        var changed = GetPodUploadStatusHandler.BuildCacheScopeKey(
            includeCreditNoteActivity: false,
            isPodOperator: false,
            ["c001"]);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, changed);
        Assert.StartsWith("driver-", first);
    }

    [Fact]
    public void Live_only_report_variants_do_not_receive_a_cache_scope()
    {
        Assert.Null(GetPodUploadStatusHandler.BuildCacheScopeKey(
            includeCreditNoteActivity: true,
            isPodOperator: false,
            assignedCustomerCodes: null));
        Assert.Null(GetPodUploadStatusHandler.BuildCacheScopeKey(
            includeCreditNoteActivity: false,
            isPodOperator: true,
            assignedCustomerCodes: null));
    }

    private PodReportCacheStore CreateStore() =>
        new(
            _context,
            Options.Create(new PodReportCacheSettings
            {
                Enabled = true,
                FreshnessMinutes = 15,
                RetentionDays = 7
            }),
            NullLogger<PodReportCacheStore>.Instance);
}
