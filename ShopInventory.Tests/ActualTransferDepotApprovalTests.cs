using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Commands.CreateInventoryTransfer;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the approval gate for every actual transfer raised by a depot controller.
/// </summary>
public sealed class ActualTransferDepotApprovalTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ActualTransferDepotApprovalTests()
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
    public async Task Depot_transfer_between_assigned_warehouses_requires_approval()
    {
        var controller = await AddDepotControllerAsync("WH01", "WH02");
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(
            new CreateInventoryTransferCommand(Request("WH01", "WH02"), controller.Id), default);

        Assert.False(result.IsError);
        Assert.True(result.Value.RequiresApproval);
        Assert.Null(result.Value.Transfer);
        Assert.NotNull(result.Value.PendingTransfer);
        Assert.Equal(0, sap.TransfersCreated);
        Assert.Single(_context.PendingInventoryTransfers);
        Assert.Equal(ApprovalRequestStatuses.Pending, Assert.Single(_context.ApprovalRequests).Status);
    }

    [Fact]
    public async Task Depot_transfer_to_an_unassigned_warehouse_requires_approval()
    {
        var controller = await AddDepotControllerAsync("WH01");
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(
            new CreateInventoryTransferCommand(Request("WH01", "WH99"), controller.Id), default);

        Assert.False(result.IsError);
        Assert.True(result.Value.RequiresApproval);
        Assert.NotNull(result.Value.PendingTransfer);
        Assert.Equal(0, sap.TransfersCreated);
        Assert.Single(_context.PendingInventoryTransfers);
        var approval = Assert.Single(_context.ApprovalRequests);
        Assert.Equal("Depot Controller Direct Transfers", approval.TemplateName);
        Assert.Equal(ApprovalRequestStatuses.Pending, approval.Status);
    }

    [Fact]
    public async Task An_unassigned_line_destination_cannot_bypass_approval()
    {
        var controller = await AddDepotControllerAsync("WH01", "WH02");
        var sap = new RecordingSapClient();
        var request = Request("WH01", "WH02");
        request.Lines![0].ToWarehouseCode = "WH99";

        var result = await Handler(sap).Handle(
            new CreateInventoryTransferCommand(request, controller.Id), default);

        Assert.False(result.IsError);
        Assert.True(result.Value.RequiresApproval);
        Assert.Equal(0, sap.TransfersCreated);
    }

    private CreateInventoryTransferHandler Handler(RecordingSapClient sap) =>
        new(
            _context,
            sap.AsClient(),
            StubProxy.For<IStockValidationService>((method, _) => method.Name switch
            {
                nameof(IStockValidationService.ValidateInventoryTransferStockAsync) =>
                    (object)Task.FromResult(StockValidationResult.Success()),
                _ => throw new InvalidOperationException($"Unexpected stock-validation call: {method.Name}")
            }),
            new InventoryTransferApprovalService(
                _context,
                new NoOpNotificationService(),
                NullLogger<InventoryTransferApprovalService>.Instance),
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            StubProxy.Unused<IIdempotencyRequestStore>(),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<CreateInventoryTransferHandler>.Instance);

    private async Task<User> AddDepotControllerAsync(params string[] warehouseCodes)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"depot-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "x",
            Role = ApplicationRoles.DepotController,
            IsActive = true
        };
        user.SetWarehouseCodes(warehouseCodes.ToList());
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private static CreateInventoryTransferRequest Request(string fromWarehouse, string toWarehouse) => new()
    {
        FromWarehouse = fromWarehouse,
        ToWarehouse = toWarehouse,
        Lines =
        [
            new CreateInventoryTransferLineRequest
            {
                ItemCode = "ITEM-1",
                Quantity = 1,
                UoMCode = "EA",
                FromWarehouseCode = fromWarehouse,
                ToWarehouseCode = toWarehouse
            }
        ]
    };

    private sealed class RecordingSapClient
    {
        public int TransfersCreated { get; private set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetWarehousesAsync) =>
                    (object)Task.FromResult(new List<WarehouseDto>
                    {
                        Warehouse("WH01"),
                        Warehouse("WH02"),
                        Warehouse("WH99")
                    }),
                nameof(ISAPServiceLayerClient.CreateInventoryTransferAsync) =>
                    Create((CreateInventoryTransferRequest)args![0]!),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<InventoryTransfer> Create(CreateInventoryTransferRequest request)
        {
            TransfersCreated++;
            return Task.FromResult(new InventoryTransfer
            {
                DocEntry = 7001,
                DocNum = 7001,
                FromWarehouse = request.FromWarehouse,
                ToWarehouse = request.ToWarehouse
            });
        }

        private static WarehouseDto Warehouse(string code) => new()
        {
            WarehouseCode = code,
            WarehouseName = code,
            IsActive = true
        };
    }
}
