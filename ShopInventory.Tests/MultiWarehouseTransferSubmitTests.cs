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
/// Submitting a direct transfer whose lines draw from several warehouses — the form's Dispatch → KEFSHOP
/// case — through the real handler and approval engine, with SAP stubbed to its warehouse list.
/// </summary>
/// <remarks>
/// The engine's own tests build the held transfer by hand. These go through what the form actually
/// calls, so they catch the handler routing on the header pair, or letting a line move stock nowhere,
/// which the engine tests cannot see.
/// </remarks>
public sealed class MultiWarehouseTransferSubmitTests : IDisposable
{
    private static readonly string[] SapWarehouses = ["DISPCH", "DISPYOG", "DISPMILK", "DISPICE", "KEFSHOP"];

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public MultiWarehouseTransferSubmitTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Lines_from_two_dispatch_warehouses_are_held_for_the_factory_shops_stock_officer()
    {
        var approval = ApprovalService();
        await SetUpFactoryShopRouteAsync(approval);
        var submitter = await AddUserAsync(ApplicationRoles.StockController, "DISPCH");

        var result = await Handler(approval).Handle(new CreateInventoryTransferCommand(Request(
            ("DISPCH", "KEFSHOP"), ("DISPYOG", "KEFSHOP")), submitter.Id), default);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var held = Assert.IsType<PendingInventoryTransferDto>(result.Value.PendingTransfer);
        Assert.Equal(["DISPCH", "DISPYOG"], held.FromWarehouses);
        Assert.Equal(["KEFSHOP"], held.ToWarehouses);

        var request = await _context.ApprovalRequests.AsNoTracking().SingleAsync();
        Assert.Equal("Dispatch to Factory Shop", request.TemplateName);
        Assert.Equal("DISPCH, DISPYOG", request.FromWarehouse);
    }

    [Fact]
    public async Task A_line_whose_source_is_its_destination_is_refused()
    {
        var approval = ApprovalService();
        await SetUpFactoryShopRouteAsync(approval);
        var submitter = await AddUserAsync(ApplicationRoles.StockController, "DISPYOG");

        // The header reads DISPYOG to KEFSHOP and differs; the second line does not.
        var result = await Handler(approval).Handle(new CreateInventoryTransferCommand(Request(
            ("DISPYOG", "KEFSHOP"), ("KEFSHOP", "KEFSHOP")), submitter.Id), default);

        Assert.True(result.IsError);
        Assert.Contains("Line 2", result.FirstError.Description);
        Assert.Empty(await _context.PendingInventoryTransfers.AsNoTracking().ToListAsync());
    }

    private static async Task SetUpFactoryShopRouteAsync(IInventoryTransferApprovalService approval)
    {
        var stage = await approval.SaveStageAsync(new ApprovalStageDefinitionDto
        {
            Name = "Receiving stock officer",
            AuthorizerRoles = [ApplicationRoles.StockController],
            WarehouseScope = ApprovalWarehouseScopes.Destination,
            ApprovalPerWarehouse = true
        }, default);
        var template = await approval.SaveTemplateAsync(new ApprovalTemplateDefinitionDto
        {
            Name = "Dispatch to Factory Shop",
            DocumentType = ApprovalDocumentTypes.InventoryTransfer,
            StageIds = [stage.Value.Id],
            FromWarehouses = ["DISPCH", "DISPYOG", "DISPMILK", "DISPICE"],
            ToWarehouses = ["KEFSHOP"],
            Priority = 200
        }, default);
        Assert.False(template.IsError);
    }

    private static CreateInventoryTransferRequest Request(params (string From, string To)[] lines) => new()
    {
        FromWarehouse = lines[0].From,
        ToWarehouse = lines[0].To,
        DocDate = "2026-11-05",
        Comments = "Reason: Thursday top-up",
        Lines = lines.Select((line, index) => new CreateInventoryTransferLineRequest
        {
            ItemCode = $"ITEM{index:000}",
            Quantity = 5,
            UoMCode = "EA",
            FromWarehouseCode = line.From,
            ToWarehouseCode = line.To
        }).ToList()
    };

    private CreateInventoryTransferHandler Handler(IInventoryTransferApprovalService approval) => new(
        _context,
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name == nameof(ISAPServiceLayerClient.GetWarehousesAsync)
            ? Task.FromResult(SapWarehouses.Select(code => new WarehouseDto { WarehouseCode = code }).ToList())
            : null),
        StubProxy.For<IStockValidationService>((method, _) => method.Name == nameof(IStockValidationService.ValidateInventoryTransferStockAsync)
            ? Task.FromResult(new StockValidationResult())
            : null),
        approval,
        StubProxy.For<IAuditService>((method, _) => method.ReturnType == typeof(Task) ? Task.CompletedTask : null),
        // No ClientRequestId, so the idempotency store is never asked.
        StubProxy.Unused<IIdempotencyRequestStore>(),
        Options.Create(new SAPSettings { Enabled = true, TransferStockValidationBudgetSeconds = 5 }),
        NullLogger<CreateInventoryTransferHandler>.Instance);

    private IInventoryTransferApprovalService ApprovalService() => new InventoryTransferApprovalService(
        _context, new NoOpNotificationService(), NullLogger<InventoryTransferApprovalService>.Instance);

    private async Task<User> AddUserAsync(string role, params string[] warehouseCodes)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"{role.ToLowerInvariant()}-{suffix}",
            Email = $"{role.ToLowerInvariant()}-{suffix}@example.test",
            PasswordHash = "x",
            Role = role,
            IsActive = true
        };
        user.SetWarehouseCodes(warehouseCodes.ToList());
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }
}
