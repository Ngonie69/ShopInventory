using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Automatic serial number selection for invoice lines. A serial number is one physical unit, so
/// a line needs exactly as many as it has units and no two lines may name the same one — SAP
/// rejects anything else with "Cannot add row without complete selection of batch/serial numbers".
/// </summary>
public sealed class InvoiceSerialAllocationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public InvoiceSerialAllocationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
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
    public async Task A_serial_managed_line_is_given_one_serial_number_per_unit()
    {
        var service = CreateService(SerialStock("S-3", "S-1", "S-2"));

        var result = await service.ValidateAndAllocateBatchesAsync(Invoice(("ITEM-S", 2)));

        Assert.Empty(result.ValidationErrors);
        var line = Assert.Single(result.AllocatedLines);
        Assert.True(line.IsSerialManaged);
        Assert.Equal(["S-1", "S-2"], line.Serials.Select(s => s.InternalSerialNumber));
    }

    [Fact]
    public async Task Two_lines_for_the_same_item_are_given_different_serial_numbers()
    {
        var service = CreateService(SerialStock("S-1", "S-2", "S-3"));

        var result = await service.ValidateAndAllocateBatchesAsync(Invoice(("ITEM-S", 2), ("ITEM-S", 1)));

        Assert.Empty(result.ValidationErrors);
        Assert.Equal(["S-1", "S-2"], result.AllocatedLines[0].Serials.Select(s => s.InternalSerialNumber));
        Assert.Equal(["S-3"], result.AllocatedLines[1].Serials.Select(s => s.InternalSerialNumber));
    }

    [Fact]
    public async Task A_line_the_remaining_serial_numbers_cannot_cover_is_reported()
    {
        var service = CreateService(SerialStock("S-1", "S-2"));

        var result = await service.ValidateAndAllocateBatchesAsync(Invoice(("ITEM-S", 2), ("ITEM-S", 1)));

        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal(BatchValidationErrorCode.InsufficientTotalStock, error.ErrorCode);
        Assert.Contains("Need 1, available 0", error.Message);
    }

    [Fact]
    public async Task A_serial_managed_line_cannot_carry_a_fractional_quantity()
    {
        var service = CreateService(SerialStock("S-1", "S-2"));

        var result = await service.ValidateAndAllocateBatchesAsync(Invoice(("ITEM-S", 1.5m)));

        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal(BatchValidationErrorCode.InvalidQuantity, error.ErrorCode);
        Assert.Contains("whole number of units", error.Message);
    }

    [Fact]
    public async Task A_selection_the_caller_supplied_is_kept_and_checked_against_the_quantity()
    {
        var service = CreateService(SerialStock("S-1", "S-2", "S-3"));
        var request = Invoice(("ITEM-S", 2));
        request.Lines![0].SerialNumbers =
        [
            new() { InternalSerialNumber = "S-3" },
            new() { InternalSerialNumber = "S-1" }
        ];

        var result = await service.ValidateAndAllocateBatchesAsync(request);

        Assert.Empty(result.ValidationErrors);
        Assert.Equal(["S-3", "S-1"], result.AllocatedLines[0].Serials.Select(s => s.InternalSerialNumber));
    }

    [Fact]
    public async Task A_selection_short_of_the_line_quantity_is_reported()
    {
        var service = CreateService(SerialStock("S-1", "S-2"));
        var request = Invoice(("ITEM-S", 2));
        request.Lines![0].SerialNumbers = [new() { InternalSerialNumber = "S-1" }];

        var result = await service.ValidateAndAllocateBatchesAsync(request);

        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal(BatchValidationErrorCode.BatchQuantityMismatch, error.ErrorCode);
        Assert.Contains("covers 1 of 2 units", error.Message);
    }

    [Fact]
    public async Task The_same_serial_number_cannot_be_named_on_two_lines()
    {
        var service = CreateService(SerialStock("S-1", "S-2"));
        var request = Invoice(("ITEM-S", 1), ("ITEM-S", 1));
        request.Lines![0].SerialNumbers = [new() { InternalSerialNumber = "S-1" }];
        request.Lines![1].SerialNumbers = [new() { InternalSerialNumber = "S-1" }];

        var result = await service.ValidateAndAllocateBatchesAsync(request);

        var error = Assert.Single(result.ValidationErrors);
        Assert.Contains("already allocated to another line", error.Message);
    }

    [Fact]
    public async Task Nothing_is_allocated_when_the_caller_turned_automatic_selection_off()
    {
        var service = CreateService(SerialStock("S-1"));

        var result = await service.ValidateAndAllocateBatchesAsync(
            Invoice(("ITEM-S", 1)), autoAllocate: false);

        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal(BatchValidationErrorCode.BatchAllocationRequired, error.ErrorCode);
        Assert.Contains("requires 1 serial number", error.Message);
    }

    private static CreateInvoiceRequest Invoice(params (string ItemCode, decimal Quantity)[] lines) =>
        new()
        {
            CardCode = "C-1",
            DocCurrency = "USD",
            Lines = lines
                .Select(line => new CreateInvoiceLineRequest
                {
                    ItemCode = line.ItemCode,
                    Quantity = line.Quantity,
                    WarehouseCode = "WH-1"
                })
                .ToList()
        };

    private static List<SerialNumber> SerialStock(params string[] serialNumbers) =>
        serialNumbers
            .Select((serial, index) => new SerialNumber
            {
                ItemCode = "ITEM-S",
                DistNumber = serial,
                InternalSerialNumber = serial,
                SystemNumber = index + 1,
                Quantity = 1,
                WhsCode = "WH-1"
            })
            .ToList();

    private BatchInventoryValidationService CreateService(List<SerialNumber> serialStock) =>
        new(
            _context,
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetItemByCodeAsync) => Task.FromResult<Item?>(new Item
                {
                    ItemCode = (string)args![0]!,
                    ManageBatchNumbers = "tNO",
                    ManageSerialNumbers = "tYES"
                }),
                nameof(ISAPServiceLayerClient.GetSerialNumbersForItemInWarehouseAsync) =>
                    Task.FromResult(serialStock),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            }),
            StubProxy.Unused<IInventoryLockService>(),
            NullLogger<BatchInventoryValidationService>.Instance);
}
