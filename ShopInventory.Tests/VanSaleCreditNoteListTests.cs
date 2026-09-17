using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The van credit notes page and the ordinary one are the same list split by
/// <see cref="VanSaleCreditNotes"/>: a credit note is a van's when a line reverses an invoice the van
/// app raised. Each of the list's three data paths is asserted, because each applies the rule its own
/// way and a path that drops it shows a van's credit notes on the wrong page with nothing looking amiss.
/// </summary>
public sealed class VanSaleCreditNoteListTests : IDisposable
{
    // Invoice DocEntries, one per way an invoice can come to exist.
    private const int VanReservationInvoice = 501;
    private const int VanOnlineDesktopInvoice = 502;
    private const int TillInvoice = 503;
    private const int PendingVanReservationInvoice = 504;

    // Credit note DocNums, named for what they reverse.
    private const int AgainstVanReservation = 9101;
    private const int AgainstVanDesktopSale = 9102;
    private const int AgainstTillSale = 9103;
    private const int AgainstNoInvoice = 9104;
    private const int AgainstUnconfirmedReservation = 9105;

    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSaleCreditNoteListTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
        SeedInvoiceSources();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- The projection: the path the portal reads day to day ---

    [Fact]
    public async Task Projection_van_only_returns_credit_notes_against_van_reservation_and_van_desktop_invoices()
    {
        await SeedProjectionAsync();

        var response = await ServiceReadingProjection().GetAllAsync(
            page: 1, pageSize: 25, fromDate: From, toDate: To, vanSalesOnly: true);

        Assert.Equal(
            [AgainstVanReservation, AgainstVanDesktopSale],
            response.CreditNotes.Select(note => note.SAPDocNum!.Value).Order().ToList());
        Assert.Equal(2, response.TotalCount);
    }

    [Fact]
    public async Task Projection_excluding_van_sales_keeps_till_unbased_and_unconfirmed_credit_notes()
    {
        await SeedProjectionAsync();

        var response = await ServiceReadingProjection().GetAllAsync(
            page: 1, pageSize: 25, fromDate: From, toDate: To, vanSalesOnly: false);

        Assert.Equal(
            [AgainstTillSale, AgainstNoInvoice, AgainstUnconfirmedReservation],
            response.CreditNotes.Select(note => note.SAPDocNum!.Value).Order().ToList());
        Assert.Equal(3, response.TotalCount);
    }

    [Fact]
    public async Task Projection_without_a_van_filter_returns_every_credit_note()
    {
        await SeedProjectionAsync();

        var response = await ServiceReadingProjection().GetAllAsync(
            page: 1, pageSize: 25, fromDate: From, toDate: To);

        Assert.Equal(5, response.TotalCount);
    }

    [Fact]
    public async Task Projection_counts_the_van_filter_before_paging()
    {
        await SeedProjectionAsync();

        var response = await ServiceReadingProjection().GetAllAsync(
            page: 2, pageSize: 2, fromDate: From, toDate: To, vanSalesOnly: false);

        Assert.Equal(3, response.TotalCount);
        Assert.Equal(2, response.TotalPages);
        Assert.Single(response.CreditNotes);
    }

    // --- SAP: read when the projection is not ready ---

    [Fact]
    public async Task Sap_fallback_splits_credit_notes_by_the_invoices_their_lines_are_based_on()
    {
        var service = ServiceReadingSap(
        [
            SapCreditNote(7001, AgainstVanReservation, Line(13, VanReservationInvoice)),
            SapCreditNote(7002, AgainstVanDesktopSale, Line(13, TillInvoice), Line(13, VanOnlineDesktopInvoice)),
            SapCreditNote(7003, AgainstTillSale, Line(13, TillInvoice)),
            SapCreditNote(7004, AgainstNoInvoice, Line(null, null), Line(17, VanReservationInvoice)),
            SapCreditNote(7005, AgainstUnconfirmedReservation, Line(13, PendingVanReservationInvoice))
        ]);

        var vanOnly = await service.GetAllAsync(page: 1, pageSize: 25, fromDate: From, toDate: To, vanSalesOnly: true);
        var notVan = await service.GetAllAsync(page: 1, pageSize: 25, fromDate: From, toDate: To, vanSalesOnly: false);
        var all = await service.GetAllAsync(page: 1, pageSize: 25, fromDate: From, toDate: To);

        Assert.Equal([AgainstVanReservation, AgainstVanDesktopSale], DocNums(vanOnly));
        Assert.Equal(2, vanOnly.TotalCount);
        Assert.Equal([AgainstTillSale, AgainstNoInvoice, AgainstUnconfirmedReservation], DocNums(notVan));
        Assert.Equal(3, notVan.TotalCount);
        Assert.Equal(5, all.TotalCount);
    }

    // --- The local table: read when SAP is unreachable too ---

    [Fact]
    public async Task Local_fallback_splits_credit_notes_by_the_invoice_they_reverse()
    {
        SeedLocalCreditNote("CN-VAN-RES", VanReservationInvoice);
        SeedLocalCreditNote("CN-VAN-DESK", VanOnlineDesktopInvoice);
        SeedLocalCreditNote("CN-TILL", TillInvoice);
        SeedLocalCreditNote("CN-NONE", null);
        SeedLocalCreditNote("CN-PENDING", PendingVanReservationInvoice);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = ServiceWithSapDown();

        var vanOnly = await service.GetAllAsync(page: 1, pageSize: 25, fromDate: From, toDate: To, vanSalesOnly: true);
        var notVan = await service.GetAllAsync(page: 1, pageSize: 25, fromDate: From, toDate: To, vanSalesOnly: false);
        var all = await service.GetAllAsync(page: 1, pageSize: 25, fromDate: From, toDate: To);

        Assert.Equal(["CN-VAN-DESK", "CN-VAN-RES"], Numbers(vanOnly));
        Assert.Equal(2, vanOnly.TotalCount);
        Assert.Equal(["CN-NONE", "CN-PENDING", "CN-TILL"], Numbers(notVan));
        Assert.Equal(3, notVan.TotalCount);
        Assert.Equal(5, all.TotalCount);
    }

    // --- Fixtures ---

    /// <summary>One invoice per source: two van, one till, one van reservation never confirmed.</summary>
    private void SeedInvoiceSources()
    {
        _context.StockReservations.Add(Reservation("VAN-RES-1", ReservationStatus.Confirmed, VanReservationInvoice));
        // A reservation that never confirmed posted nothing, whatever DocEntry it may carry.
        _context.StockReservations.Add(Reservation("VAN-RES-2", ReservationStatus.Cancelled, PendingVanReservationInvoice));
        _context.DesktopSales.Add(DesktopSale("VAN-ONLINE-1", SaleSourceSystems.VanSalesOnline, VanOnlineDesktopInvoice));
        _context.DesktopSales.Add(DesktopSale("TILL-1", SaleSourceSystems.ShopTill, TillInvoice));
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private async Task SeedProjectionAsync()
    {
        SeedSnapshot(7001, AgainstVanReservation, (13, VanReservationInvoice));
        // Mixed: one line off a till invoice, one off a van invoice — any van line makes it a van's.
        SeedSnapshot(7002, AgainstVanDesktopSale, (13, TillInvoice), (13, VanOnlineDesktopInvoice));
        SeedSnapshot(7003, AgainstTillSale, (13, TillInvoice));
        // No invoice base; and a non-invoice base whose entry happens to equal a van invoice's.
        SeedSnapshot(7004, AgainstNoInvoice, (null, null), (17, VanReservationInvoice));
        SeedSnapshot(7005, AgainstUnconfirmedReservation, (13, PendingVanReservationInvoice));
        MarkProjectionReadyForReads();
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static StockReservationEntity Reservation(string reference, string status, int docEntry) => new()
    {
        ReservationId = Guid.NewGuid().ToString(),
        ExternalReferenceId = reference,
        SourceSystem = SaleSourceSystems.VanSales,
        DocumentType = ReservationDocumentType.Invoice,
        CardCode = "C001",
        Status = status,
        SAPDocEntry = docEntry,
        ExpiresAt = DateTime.UtcNow.AddHours(1)
    };

    private static DesktopSaleEntity DesktopSale(string reference, string source, int docEntry) => new()
    {
        ExternalReferenceId = reference,
        SourceSystem = source,
        CardCode = "C001",
        WarehouseCode = "VAN01",
        DocDate = new DateTime(2026, 7, 5),
        SapDocEntry = docEntry,
        CreatedAt = DateTime.UtcNow
    };

    private void SeedSnapshot(int docEntry, int docNum, params (int? BaseType, int? BaseEntry)[] lines)
    {
        _context.SapCreditNoteSnapshots.Add(new SapCreditNoteSnapshotEntity
        {
            SapDocEntry = docEntry,
            SapDocNum = docNum,
            DocDate = DateTime.SpecifyKind(new DateTime(2026, 7, 10), DateTimeKind.Utc),
            CardCode = "C001",
            DocCurrency = "USD",
            DocTotal = 10m,
            DocumentStatus = "bost_Open",
            LastSeenInSapAtUtc = DateTime.UtcNow,
            SyncedAtUtc = DateTime.UtcNow,
            Lines = lines.Select((line, index) => new SapCreditNoteLineSnapshotEntity
            {
                CreditNoteDocEntry = docEntry,
                LineNum = index,
                BaseType = line.BaseType,
                BaseEntry = line.BaseEntry,
                BaseLine = line.BaseEntry is null ? null : 0,
                LineTotal = 5m
            }).ToList()
        });
    }

    private void SeedLocalCreditNote(string number, int? originalInvoiceDocEntry)
    {
        _context.CreditNotes.Add(new CreditNoteEntity
        {
            CreditNoteNumber = number,
            CreditNoteDate = DateTime.SpecifyKind(new DateTime(2026, 7, 10), DateTimeKind.Utc),
            CardCode = "C001",
            OriginalInvoiceDocEntry = originalInvoiceDocEntry,
            Status = CreditNoteStatus.Approved,
            DocTotal = 10m
        });
    }

    private static SAPCreditNote SapCreditNote(int docEntry, int docNum, params SAPCreditNoteLine[] lines) => new()
    {
        DocEntry = docEntry,
        DocNum = docNum,
        DocDate = "2026-07-10",
        CardCode = "C001",
        DocCurrency = "USD",
        DocTotal = 10,
        DocumentStatus = "bost_Open",
        Cancelled = "tNO",
        DocumentLines = lines.ToList()
    };

    private static SAPCreditNoteLine Line(int? baseType, int? baseEntry) => new()
    {
        BaseType = baseType,
        BaseEntry = baseEntry,
        BaseLine = baseEntry is null ? null : 0,
        LineTotal = 5
    };

    private static List<int> DocNums(DTOs.CreditNoteListResponseDto response) =>
        response.CreditNotes.Select(note => note.SAPDocNum!.Value).ToList();

    private static List<string> Numbers(DTOs.CreditNoteListResponseDto response) =>
        response.CreditNotes.Select(note => note.CreditNoteNumber).Order(StringComparer.Ordinal).ToList();

    private void MarkProjectionReadyForReads()
    {
        _context.CacheSyncStates.Add(new CacheSyncStateEntity
        {
            CacheKey = CreditNoteProjectionSyncService.CacheKey,
            DisplayName = "Credit Notes",
            LastSyncedAt = DateTime.UtcNow.AddMinutes(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _context.SystemConfigs.Add(new SystemConfigEntity
        {
            Key = "CreditNoteSync.Checkpoint",
            ValueType = "json",
            Category = "Synchronization",
            IsEditable = false,
            UpdatedAt = DateTime.UtcNow,
            Value = """{"BackfillCompleted":true,"LastUpdateWatermarkDate":"2026-07-30T00:00:00Z"}"""
        });
    }

    /// <summary>Projection ready; SAP throws on any call, so reaching it fails the test.</summary>
    private CreditNoteService ServiceReadingProjection() =>
        CreateCreditNoteService(StubProxy.Unused<ISAPServiceLayerClient>());

    /// <summary>Projection never synced, so the list reads SAP, which answers with these.</summary>
    private CreditNoteService ServiceReadingSap(List<SAPCreditNote> creditNotes) =>
        CreateCreditNoteService(StubProxy.For<ISAPServiceLayerClient>((method, _) =>
            method.Name == nameof(ISAPServiceLayerClient.GetCreditNotesByDateRangeAsync)
                ? Task.FromResult(creditNotes.ToList())
                : throw new InvalidOperationException($"{method.Name} was not expected to be called.")));

    /// <summary>Projection never synced and SAP unreachable, so the list reads the local table.</summary>
    private CreditNoteService ServiceWithSapDown() =>
        CreateCreditNoteService(StubProxy.For<ISAPServiceLayerClient>((method, _) =>
            method.Name == nameof(ISAPServiceLayerClient.GetCreditNotesByDateRangeAsync)
                ? Task.FromException<List<SAPCreditNote>>(new HttpRequestException("SAP is down"))
                : throw new InvalidOperationException($"{method.Name} was not expected to be called.")));

    private CreditNoteService CreateCreditNoteService(ISAPServiceLayerClient sapClient) =>
        new(
            _context,
            sapClient,
            StubProxy.Unused<IFiscalizationService>(),
            new CreditNoteProjectionSyncService(
                _context,
                StubProxy.Unused<ISAPServiceLayerClient>(),
                Options.Create(new CreditNoteSyncSettings { Enabled = true, StaleAfterMinutes = 10 }),
                NullLogger<CreditNoteProjectionSyncService>.Instance),
            StubProxy.Unused<IStockLedger>(),
            Options.Create(new FiscalisationSettings()),
            NullLogger<CreditNoteService>.Instance);
}
