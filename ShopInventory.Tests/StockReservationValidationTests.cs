using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers how <see cref="StockReservationService.ValidateStockAvailabilityAsync"/> reads stock.
/// </summary>
/// <remarks>
/// It used to call GetStockQuantitiesInWarehouseAsync once per line — a scan of OITM joined to OITW
/// for every item in the warehouse, paged 500 rows at a time — and then pick one item out of the
/// result with FirstOrDefault. A twenty-line request against a five-thousand-item warehouse spent
/// roughly 220 SAP round-trips answering twenty single-item questions. These tests pin the read
/// pattern, not just the answer, because the answer was never wrong — only the cost was.
/// </remarks>
public sealed class StockReservationValidationTests : IDisposable
{
    private const string Warehouse = "WH01";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly FakeSapStock _sap = new();

    public StockReservationValidationTests()
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
    public async Task Many_lines_in_one_warehouse_cost_one_stock_read()
    {
        _sap.Stock[("WH01", "ITEM-A")] = (InStock: 100m, Committed: 0m);
        _sap.Stock[("WH01", "ITEM-B")] = (InStock: 100m, Committed: 0m);
        _sap.Stock[("WH01", "ITEM-C")] = (InStock: 100m, Committed: 0m);

        var (isValid, errors) = await ValidateAsync(
            Line(1, "ITEM-A", 1m),
            Line(2, "ITEM-B", 1m),
            Line(3, "ITEM-C", 1m));

        Assert.True(isValid);
        Assert.Empty(errors);
        Assert.Equal(0, _sap.WholeWarehouseReads);
        Assert.Single(_sap.ScopedReads);
        Assert.Equal(["ITEM-A", "ITEM-B", "ITEM-C"], _sap.ScopedReads[0].ItemCodes.Order());
    }

    [Fact]
    public async Task Lines_across_warehouses_cost_one_stock_read_each()
    {
        _sap.Stock[("WH01", "ITEM-A")] = (InStock: 100m, Committed: 0m);
        _sap.Stock[("WH02", "ITEM-B")] = (InStock: 100m, Committed: 0m);

        var (isValid, _) = await ValidateAsync(
            Line(1, "ITEM-A", 1m),
            Line(2, "ITEM-B", 1m, warehouse: "WH02"));

        Assert.True(isValid);
        Assert.Equal(0, _sap.WholeWarehouseReads);
        Assert.Equal(2, _sap.ScopedReads.Count);
    }

    [Fact]
    public async Task Insufficient_stock_is_still_reported()
    {
        _sap.Stock[("WH01", "ITEM-A")] = (InStock: 10m, Committed: 4m);

        var (isValid, errors) = await ValidateAsync(Line(1, "ITEM-A", 7m));

        Assert.False(isValid);
        var error = Assert.Single(errors);
        Assert.Equal(ReservationErrorCode.InsufficientStock, error.ErrorCode);
        Assert.Equal(6m, error.AvailableQuantity);
    }

    [Fact]
    public async Task Item_absent_from_the_warehouse_is_still_reported()
    {
        var (isValid, errors) = await ValidateAsync(Line(1, "GHOST", 1m));

        Assert.False(isValid);
        var error = Assert.Single(errors);
        Assert.Equal(ReservationErrorCode.ItemNotFound, error.ErrorCode);
    }

    [Fact]
    public async Task Repeated_item_across_lines_is_summed_without_a_second_read()
    {
        // Two lines for the same item: 6 + 6 fits neither together nor against 10 on hand. The
        // aggregate pass used to re-read the whole warehouse to work that out.
        _sap.Stock[("WH01", "ITEM-A")] = (InStock: 10m, Committed: 0m);

        var (isValid, errors) = await ValidateAsync(
            Line(1, "ITEM-A", 6m),
            Line(2, "ITEM-A", 6m));

        Assert.False(isValid);
        Assert.Contains(errors, error => error.ErrorCode == ReservationErrorCode.InsufficientStock);
        Assert.Single(_sap.ScopedReads);
    }

    private async Task<(bool IsValid, List<StockReservationErrorDto> Errors)> ValidateAsync(
        params CreateStockReservationLineRequest[] lines)
    {
        var service = new StockReservationService(
            _context,
            _sap.AsClient(),
            StubProxy.For<IBatchInventoryValidationService>((method, _) => method.Name switch
            {
                // No UoM given on these lines, and nothing here is batch managed.
                nameof(IBatchInventoryValidationService.IsBatchManagedItemAsync) => Task.FromResult(false),
                _ => throw new InvalidOperationException($"Unexpected call: {method.Name}")
            }),
            StubProxy.Unused<IInventoryLockService>(),
            StubProxy.Unused<IInvoiceFiscalizationQueue>(),
            StubProxy.Unused<INotificationService>(),
            NullLogger<StockReservationService>.Instance);

        return await service.ValidateStockAvailabilityAsync([.. lines]);
    }

    private static CreateStockReservationLineRequest Line(
        int lineNum,
        string itemCode,
        decimal quantity,
        string warehouse = Warehouse) =>
        new()
        {
            LineNum = lineNum,
            ItemCode = itemCode,
            Quantity = quantity,
            WarehouseCode = warehouse
        };

    /// <summary>
    /// Records how stock was asked for, so the tests can assert on the read pattern and not only on
    /// the verdict.
    /// </summary>
    private sealed class FakeSapStock
    {
        public Dictionary<(string Warehouse, string ItemCode), (decimal InStock, decimal Committed)> Stock { get; }
            = new();

        public List<(string Warehouse, string[] ItemCodes)> ScopedReads { get; } = [];

        public int WholeWarehouseReads { get; private set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync) =>
                    ScopedRead((string)args![0]!, (IEnumerable<string>)args[1]!),
                nameof(ISAPServiceLayerClient.GetStockQuantitiesInWarehouseAsync) =>
                    WholeWarehouseRead((string)args![0]!),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<List<StockQuantityDto>> ScopedRead(string warehouse, IEnumerable<string> itemCodes)
        {
            var codes = itemCodes.ToArray();
            ScopedReads.Add((warehouse, codes));
            return Task.FromResult(Rows(warehouse, codes));
        }

        private Task<List<StockQuantityDto>> WholeWarehouseRead(string warehouse)
        {
            WholeWarehouseReads++;
            var codes = Stock.Keys
                .Where(key => key.Warehouse == warehouse)
                .Select(key => key.ItemCode)
                .ToArray();
            return Task.FromResult(Rows(warehouse, codes));
        }

        private List<StockQuantityDto> Rows(string warehouse, IEnumerable<string> codes) =>
            [.. codes
                .Where(code => Stock.ContainsKey((warehouse, code)))
                .Select(code => new StockQuantityDto
                {
                    ItemCode = code,
                    ItemName = code,
                    WarehouseCode = warehouse,
                    InStock = Stock[(warehouse, code)].InStock,
                    Committed = Stock[(warehouse, code)].Committed
                })];
    }
}
