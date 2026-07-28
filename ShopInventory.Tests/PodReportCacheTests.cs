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
            Options.Create(new CreditNoteSyncSettings()),
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
    public async Task Cached_report_is_reenriched_from_the_fresh_credit_note_projection()
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
                CreditNoteDataComplete = true,
                Items =
                [
                    new PodUploadStatusItemDto
                    {
                        DocEntry = 123,
                        DocNum = 456,
                        DocTotal = 115,
                        CardCode = "C001",
                        CardName = "Projected customer"
                    }
                ]
            },
            CancellationToken.None);

        _context.SapCreditNoteSnapshots.Add(new SapCreditNoteSnapshotEntity
        {
            SapDocEntry = 7001,
            SapDocNum = 9001,
            DocDate = new DateTime(2026, 7, 20),
            SyncedAtUtc = DateTime.UtcNow,
            LastSeenInSapAtUtc = DateTime.UtcNow,
            Lines =
            [
                new SapCreditNoteLineSnapshotEntity
                {
                    CreditNoteDocEntry = 7001,
                    LineNum = 0,
                    BaseType = 13,
                    BaseEntry = 123,
                    LineTotal = 100,
                    VatSum = 15,
                    CreditReason = "Returned"
                }
            ]
        });
        _context.CacheSyncStates.Add(new CacheSyncStateEntity
        {
            CacheKey = CreditNoteProjectionSyncService.CacheKey,
            DisplayName = "Credit Notes",
            LastSyncedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var documentService = StubProxy.For<IDocumentService>((method, _) =>
            method.Name == nameof(IDocumentService.GetPodStatusByDocEntriesAsync)
                ? Task.FromResult(new Dictionary<int, PodStatusInfo>())
                : throw new InvalidOperationException($"IDocumentService.{method.Name} was not expected."));
        var handler = new GetPodUploadStatusHandler(
            StubProxy.Unused<ISAPServiceLayerClient>(),
            documentService,
            _context,
            Options.Create(new SAPSettings { Enabled = false }),
            Options.Create(new CreditNoteSyncSettings
            {
                Enabled = true,
                UseForPodReports = true,
                StaleAfterMinutes = 10
            }),
            store,
            NullLogger<GetPodUploadStatusHandler>.Instance);

        var result = await handler.Handle(
            new GetPodUploadStatusQuery(fromDate, toDate, UserId: null),
            CancellationToken.None);

        Assert.False(result.IsError);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal("9001", item.CreditNoteNumber);
        Assert.Equal("Returned", item.CreditNoteReason);
        Assert.True(item.IsFullyCredited);
        Assert.True(result.Value.CreditNoteDataComplete);
    }

    [Fact]
    public void Driver_scope_is_order_independent_and_changes_with_assignments()
    {
        var first = GetPodUploadStatusHandler.BuildCacheScopeKey(
            includeCreditNoteActivity: false,
            [" c002 ", "C001"]);
        var reordered = GetPodUploadStatusHandler.BuildCacheScopeKey(
            includeCreditNoteActivity: false,
            ["c001", "c002"]);
        var changed = GetPodUploadStatusHandler.BuildCacheScopeKey(
            includeCreditNoteActivity: false,
            ["c001"]);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, changed);
        Assert.StartsWith("driver-", first);
    }

    [Fact]
    public void Fresh_cache_is_served_only_when_credit_note_data_is_complete()
    {
        var completeSnapshot = new PodReportCacheSnapshot(
            new PodUploadStatusReportDto { CreditNoteDataComplete = true },
            DateTime.UtcNow,
            IsFresh: true);
        var incompleteSnapshot = new PodReportCacheSnapshot(
            new PodUploadStatusReportDto { CreditNoteDataComplete = false },
            DateTime.UtcNow,
            IsFresh: true);
        var expiredSnapshot = new PodReportCacheSnapshot(
            new PodUploadStatusReportDto { CreditNoteDataComplete = true },
            DateTime.UtcNow,
            IsFresh: false);

        Assert.True(GetPodUploadStatusHandler.CanServeCachedSnapshot(completeSnapshot));
        Assert.False(GetPodUploadStatusHandler.CanServeCachedSnapshot(incompleteSnapshot));
        Assert.False(GetPodUploadStatusHandler.CanServeCachedSnapshot(expiredSnapshot));
        Assert.False(GetPodUploadStatusHandler.CanServeCachedSnapshot(null));
    }

    [Fact]
    public void Live_only_report_variants_do_not_receive_a_cache_scope()
    {
        Assert.Null(GetPodUploadStatusHandler.BuildCacheScopeKey(
            includeCreditNoteActivity: true,
            assignedCustomerCodes: null));
    }

    [Fact]
    public void Pod_operators_share_the_global_cache_scope()
    {
        Assert.Equal(
            "global",
            GetPodUploadStatusHandler.BuildCacheScopeKey(
                includeCreditNoteActivity: false,
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
