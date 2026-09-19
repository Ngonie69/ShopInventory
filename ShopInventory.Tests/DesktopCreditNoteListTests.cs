using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The list of every desktop credit, and the retry that used to be a hand-written UPDATE.
/// </summary>
public sealed class DesktopCreditNoteListTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly RecordingSap _sap = new();
    private readonly Guid _admin = Guid.NewGuid();

    public DesktopCreditNoteListTests()
    {
        _connection.Open();
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new User { Id = _admin, Username = "admin", PasswordHash = "x", Role = "Admin", IsActive = true });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Lists_credits_from_every_sale_newest_first_with_their_sale()
    {
        var till = await GivenSaleAsync("INV1753", "KEFGRS", SaleSourceSystems.ShopTill, sapDocNum: 777347);
        var vending = await GivenSaleAsync("VND-1", "KEFVND", SaleSourceSystems.Vending);

        var older = await GivenCreditAsync(till, createdAgo: TimeSpan.FromHours(3));
        var newer = await GivenCreditAsync(vending, createdAgo: TimeSpan.FromHours(1));

        var result = await Service().ListAsync(_admin, new DesktopCreditNoteListQuery(), default);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal([newer.Number, older.Number], result.Items.Select(i => i.Number).ToArray());

        var row = result.Items[1];
        Assert.Equal("INV1753", row.SaleReference);
        Assert.Equal("KEFGRS", row.SaleWarehouseCode);
        Assert.Equal(777347, row.SaleSapDocNum);
        Assert.Equal(["KEFGRS", "KEFVND"], result.Warehouses.ToArray());
    }

    [Fact]
    public async Task Sap_counts_ignore_the_sap_filter_so_failures_stay_visible()
    {
        var sale = await GivenSaleAsync("INV1753", "KEFGRS", SaleSourceSystems.ShopTill, sapDocNum: 777347);
        await GivenCreditAsync(sale, sapStatus: DesktopCreditSapStatuses.Failed);
        await GivenCreditAsync(sale, sapStatus: DesktopCreditSapStatuses.Posted);
        await GivenCreditAsync(sale, sapStatus: DesktopCreditSapStatuses.Posted);

        var result = await Service().ListAsync(_admin,
            new DesktopCreditNoteListQuery(SapStatus: DesktopCreditSapStatuses.Posted), default);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.SapStatusCounts[DesktopCreditSapStatuses.Failed]);
        Assert.Equal(2, result.SapStatusCounts[DesktopCreditSapStatuses.Posted]);
    }

    [Fact]
    public async Task A_credit_zimra_has_not_accepted_is_not_counted_as_waiting_for_sap()
    {
        var sale = await GivenSaleAsync("INV1", "KEFGRS", SaleSourceSystems.ShopTill);
        await GivenCreditAsync(sale, status: DesktopCreditStatuses.Rejected);

        var result = await Service().ListAsync(_admin,
            new DesktopCreditNoteListQuery(SapStatus: DesktopCreditSapStatuses.Deferred), default);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.SapStatusCounts);
    }

    [Fact]
    public async Task Totals_are_kept_apart_by_currency()
    {
        var usd = await GivenSaleAsync("INV1", "KEFGRS", SaleSourceSystems.ShopTill);
        var zig = await GivenSaleAsync("INV2", "KEFGRS", SaleSourceSystems.ShopTill, currency: "ZIG");
        await GivenCreditAsync(usd, amount: 23.10m);
        await GivenCreditAsync(zig, amount: 500m);

        var result = await Service().ListAsync(_admin, new DesktopCreditNoteListQuery(), default);

        Assert.Equal(23.10m, result.TotalsByCurrency["USD"]);
        Assert.Equal(500m, result.TotalsByCurrency["ZIG"]);
    }

    [Fact]
    public async Task Search_finds_a_credit_by_its_sale_reference_or_sap_invoice_number()
    {
        var target = await GivenSaleAsync("INV1753", "KEFGRS", SaleSourceSystems.ShopTill, sapDocNum: 777347);
        var other = await GivenSaleAsync("INV9999", "KEFGRS", SaleSourceSystems.ShopTill, sapDocNum: 700001);
        await GivenCreditAsync(target);
        await GivenCreditAsync(other);

        var byReference = await Service().ListAsync(_admin, new DesktopCreditNoteListQuery(Search: "inv1753"), default);
        var byInvoice = await Service().ListAsync(_admin, new DesktopCreditNoteListQuery(Search: "777347"), default);

        Assert.Equal("INV1753", Assert.Single(byReference.Items).SaleReference);
        Assert.Equal("INV1753", Assert.Single(byInvoice.Items).SaleReference);

        // The number printed on the customer's receipt, which is the sale's id.
        var byReceiptNumber = await Service().ListAsync(_admin,
            new DesktopCreditNoteListQuery(Search: $"INV{target.Id}"), default);
        var row = Assert.Single(byReceiptNumber.Items);
        Assert.Equal("INV1753", row.SaleReference);
        Assert.Equal($"INV{target.Id}", row.SaleNumber);
    }

    [Fact]
    public async Task Both_van_source_systems_answer_the_van_filter()
    {
        await GivenCreditAsync(await GivenSaleAsync("VAN-1", "VAN01", SaleSourceSystems.VanSales));
        await GivenCreditAsync(await GivenSaleAsync("VAN-2", "VAN01", SaleSourceSystems.VanSalesOnline));
        await GivenCreditAsync(await GivenSaleAsync("TILL-1", "KEFGRS", SaleSourceSystems.ShopTill));

        var result = await Service().ListAsync(_admin,
            new DesktopCreditNoteListQuery(SourceSystem: SaleSourceSystems.VanSales), default);

        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task A_shop_bound_account_sees_only_its_own_warehouse()
    {
        await GivenCreditAsync(await GivenSaleAsync("INV1", "KEFGRS", SaleSourceSystems.ShopTill));
        await GivenCreditAsync(await GivenSaleAsync("INV2", "KEFBEL", SaleSourceSystems.ShopTill));
        var cashier = await GivenShopCashierAsync("KEFGRS");

        var result = await Service().ListAsync(cashier, new DesktopCreditNoteListQuery(), default);

        Assert.Equal("INV1", Assert.Single(result.Items).SaleReference);
        Assert.Equal(["KEFGRS"], result.Warehouses.ToArray());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().ListAsync(
            cashier, new DesktopCreditNoteListQuery(WarehouseCode: "KEFBEL"), default));
    }

    [Fact]
    public async Task Filtering_by_warehouse_keeps_every_warehouse_in_the_choices()
    {
        await GivenCreditAsync(await GivenSaleAsync("INV1", "KEFGRS", SaleSourceSystems.ShopTill));
        await GivenCreditAsync(await GivenSaleAsync("INV2", "KEFBEL", SaleSourceSystems.ShopTill));

        var result = await Service().ListAsync(_admin,
            new DesktopCreditNoteListQuery(WarehouseCode: "KEFBEL"), default);

        Assert.Equal("INV2", Assert.Single(result.Items).SaleReference);
        Assert.Equal(2, result.Warehouses.Count);
    }

    // ── Retry ────────────────────────────────────────────────────────────

    /// <summary>
    /// INV1753's credit: fiscalised, refused by SAP, and out of attempts, so the sweep had stopped
    /// looking at it. A retry resets the count and sends it now.
    /// </summary>
    [Fact]
    public async Task Retry_sends_a_capped_failed_credit_again_and_resets_its_attempts()
    {
        var sale = await GivenSaleAsync("INV1753", "KEFGRS", SaleSourceSystems.ShopTill, sapDocEntry: 4242, sapDocNum: 777347);
        var note = await GivenCreditAsync(sale, sapStatus: DesktopCreditSapStatuses.Failed, attempts: 6,
            sapError: "Cannot add row without complete selection of batch/serial numbers");

        var row = await Service().RetrySapAsync(_admin, note.Id, default);

        Assert.Single(_sap.Created);
        Assert.Equal(DesktopCreditSapStatuses.Posted, row.SapStatus);
        Assert.NotNull(row.SapDocNum);
        Assert.Null(row.SapError);
        Assert.Equal(1, row.SapAttempts);
    }

    [Theory]
    [InlineData(DesktopCreditSapStatuses.Posted, "Already in SAP")]
    [InlineData(DesktopCreditSapStatuses.Deferred, "waiting for its sale")]
    [InlineData(DesktopCreditSapStatuses.ManualInSap, "by hand")]
    public async Task Retry_refuses_a_credit_that_has_not_failed(string sapStatus, string expected)
    {
        var sale = await GivenSaleAsync("INV1", "KEFGRS", SaleSourceSystems.ShopTill, sapDocEntry: 4242, sapDocNum: 777347);
        var note = await GivenCreditAsync(sale, sapStatus: sapStatus);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service().RetrySapAsync(_admin, note.Id, default));

        Assert.Contains(expected, refusal.Message);
        Assert.Empty(_sap.Created);
    }

    [Fact]
    public async Task Retry_refuses_a_credit_zimra_has_not_accepted()
    {
        var sale = await GivenSaleAsync("INV1", "KEFGRS", SaleSourceSystems.ShopTill, sapDocEntry: 4242, sapDocNum: 777347);
        var note = await GivenCreditAsync(sale, status: DesktopCreditStatuses.ReconciliationRequired,
            sapStatus: DesktopCreditSapStatuses.Failed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().RetrySapAsync(_admin, note.Id, default));
        Assert.Empty(_sap.Created);
    }

    [Fact]
    public async Task Retry_refuses_another_shops_credit()
    {
        var sale = await GivenSaleAsync("INV1", "KEFBEL", SaleSourceSystems.ShopTill, sapDocEntry: 4242, sapDocNum: 777347);
        var note = await GivenCreditAsync(sale, sapStatus: DesktopCreditSapStatuses.Failed);
        var cashier = await GivenShopCashierAsync("KEFGRS");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().RetrySapAsync(cashier, note.Id, default));
        Assert.Empty(_sap.Created);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private DesktopCreditNoteListService Service() => new(
        _db,
        DesktopCreditPosters.Recording(_db, _sap),
        StubProxy.For<IAuditService>((m, _) => m.Name == nameof(IAuditService.LogAsync)
            ? Task.CompletedTask
            : throw new InvalidOperationException($"IAuditService.{m.Name} was not expected.")),
        NullLogger<DesktopCreditNoteListService>.Instance);

    private async Task<Guid> GivenShopCashierAsync(string warehouse)
    {
        var shop = new ShopEntity { Code = warehouse, Name = warehouse, WarehouseCode = warehouse, BusinessPartnerCode = "C-" + warehouse };
        _db.Shops.Add(shop);
        await _db.SaveChangesAsync();

        var cashier = new User
        {
            Id = Guid.NewGuid(), Username = "cashier-" + warehouse, PasswordHash = "x",
            Role = "Cashier", IsActive = true, ShopId = shop.Id
        };
        _db.Users.Add(cashier);
        await _db.SaveChangesAsync();
        return cashier.Id;
    }

    private async Task<DesktopSaleEntity> GivenSaleAsync(
        string reference, string warehouse, string source,
        int? sapDocEntry = null, int? sapDocNum = null, string currency = "USD")
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = source,
            CardCode = "CIS004",
            CardName = "Groombridge",
            DocDate = DateTime.UtcNow.Date,
            TotalAmount = 100m,
            Currency = currency,
            WarehouseCode = warehouse,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            FiscalDayNo = "537",
            FiscalReceiptNumber = "221019",
            SapDocEntry = sapDocEntry ?? (sapDocNum is null ? null : sapDocNum + 1),
            SapDocNum = sapDocNum,
            ConsolidationStatus = sapDocNum is null
                ? DesktopSaleConsolidationStatus.Pending
                : DesktopSaleConsolidationStatus.Consolidated,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0, ItemCode = "ICS025", ItemDescription = "Blueberry",
                    Quantity = 6, UnitPrice = 10m, LineTotal = 60m, WarehouseCode = warehouse
                }
            ]
        };

        _db.DesktopSales.Add(sale);
        await _db.SaveChangesAsync();
        return sale;
    }

    private async Task<DesktopCreditNoteEntity> GivenCreditAsync(
        DesktopSaleEntity sale,
        string status = DesktopCreditStatuses.Fiscalised,
        string sapStatus = DesktopCreditSapStatuses.Deferred,
        int attempts = 0,
        string? sapError = null,
        decimal? amount = null,
        TimeSpan? createdAgo = null)
    {
        var note = await DesktopCreditPosters.GivenCreditAsync(_db, sale, status);

        await _db.DesktopCreditNotes.Where(n => n.Id == note.Id).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.SapStatus, sapStatus)
            .SetProperty(n => n.SapAttempts, attempts)
            .SetProperty(n => n.SapError, sapError)
            .SetProperty(n => n.SapDocNum, sapStatus == DesktopCreditSapStatuses.Posted ? 55000 : (int?)null)
            .SetProperty(n => n.Currency, sale.Currency)
            .SetProperty(n => n.Amount, n => amount ?? n.Amount)
            .SetProperty(n => n.CreatedAtUtc, DateTime.UtcNow - (createdAgo ?? TimeSpan.Zero)));

        return await _db.DesktopCreditNotes.AsNoTracking().SingleAsync(n => n.Id == note.Id);
    }
}
