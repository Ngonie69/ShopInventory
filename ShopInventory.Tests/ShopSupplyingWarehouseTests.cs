using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Auth;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.CreateTransferRequest;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins where a till's stock request gets its source warehouse: the shop, set on the web.
/// </summary>
/// <remarks>
/// The till used to be refused with "Source warehouse is required" on every request, because it sends
/// no source — it was built to show the one the platform names at sign-in, and the platform never
/// named one. These tests hold both halves: the sign-in names it, and the request is raised against
/// it whatever the till sent.
/// </remarks>
public sealed class ShopSupplyingWarehouseTests : IDisposable
{
    private static readonly Guid Operator = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    /// <summary>What reached SAP, or null when nothing did.</summary>
    private CreateTransferRequestDto? _posted;

    public ShopSupplyingWarehouseTests()
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

    // ---- The request ----------------------------------------------------------------------------

    [Fact]
    public void A_request_naming_no_source_passes_model_validation()
    {
        // The refusal in the till's screenshot came from here, before any handler ran.
        var request = TillRequest(fromWarehouse: null);

        var valid = Validator.TryValidateObject(
            request, new ValidationContext(request), new List<ValidationResult>(), validateAllProperties: true);

        Assert.True(valid);
    }

    [Fact]
    public async Task A_till_that_sends_no_source_requests_from_its_shops_supplying_warehouse()
    {
        await GivenOperatorOnShopAsync(supplyingWarehouse: "KEFGRC");

        var result = await Handle(TillRequest(fromWarehouse: null));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.NotNull(_posted);
        Assert.Equal("KEFGRC", _posted.FromWarehouse);
        Assert.Equal("KEFGRS", _posted.ToWarehouse);
        Assert.All(_posted.Lines, line => Assert.Equal("KEFGRC", line.FromWarehouseCode));
    }

    [Fact]
    public async Task The_shops_setting_wins_over_a_stale_source_the_till_still_holds()
    {
        // Signed in before an administrator moved the shop to a different depot.
        await GivenOperatorOnShopAsync(supplyingWarehouse: "KEFBYC");

        var result = await Handle(TillRequest(fromWarehouse: "KEFGRC"));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.Equal("KEFBYC", _posted!.FromWarehouse);
        Assert.All(_posted.Lines, line => Assert.Equal("KEFBYC", line.FromWarehouseCode));
    }

    [Fact]
    public async Task A_shop_with_no_supplying_warehouse_is_refused_with_where_to_set_it()
    {
        await GivenOperatorOnShopAsync(supplyingWarehouse: null);

        // Even with a source in hand: the shop decides, and it has not.
        var result = await Handle(TillRequest(fromWarehouse: "KEFGRC"));

        Assert.True(result.IsError);
        Assert.Equal("Shops.NoSupplyingWarehouse", result.FirstError.Code);
        Assert.Contains("Graniteside Shop", result.FirstError.Description);
        Assert.Contains("Administration", result.FirstError.Description);
        Assert.Null(_posted);
    }

    [Fact]
    public async Task An_account_with_no_shop_still_requests_from_the_warehouse_it_names()
    {
        await GivenOperatorAsync(shopId: null);

        var result = await Handle(TillRequest(fromWarehouse: "KEFGRC"));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.Equal("KEFGRC", _posted!.FromWarehouse);
    }

    [Fact]
    public async Task An_account_with_no_shop_that_names_no_source_is_still_refused()
    {
        await GivenOperatorAsync(shopId: null);

        var result = await Handle(TillRequest(fromWarehouse: "  "));

        Assert.True(result.IsError);
        Assert.Equal("DesktopIntegration.SourceWarehouseRequired", result.FirstError.Code);
        Assert.Null(_posted);
    }

    // ---- The sign-in ----------------------------------------------------------------------------

    [Fact]
    public async Task Sign_in_names_the_shops_supplying_warehouse()
    {
        await GivenOperatorOnShopAsync(supplyingWarehouse: "KEFGRC");

        var info = await SignInInfoAsync();

        Assert.Equal("KEFGRC", info.TransferSourceWarehouseCode);
    }

    [Fact]
    public async Task Sign_in_names_none_for_a_shop_with_none_set()
    {
        await GivenOperatorOnShopAsync(supplyingWarehouse: null);

        var info = await SignInInfoAsync();

        // The till then shows "Assigned by the platform" and the handler's refusal explains why.
        Assert.Null(info.TransferSourceWarehouseCode);
    }

    // ---- Setup ----------------------------------------------------------------------------------

    private async Task GivenOperatorOnShopAsync(string? supplyingWarehouse)
    {
        var shop = new ShopEntity
        {
            Code = "KEFGRS",
            Name = "Graniteside Shop",
            BusinessPartnerCode = "COR007",
            WarehouseCode = "KEFGRS",
            SupplyingWarehouseCode = supplyingWarehouse,
        };

        _context.Shops.Add(shop);
        await _context.SaveChangesAsync();

        await GivenOperatorAsync(shop.Id);
    }

    private async Task GivenOperatorAsync(int? shopId)
    {
        _context.Users.Add(new User
        {
            Id = Operator,
            Username = "till1",
            FirstName = "Rudo",
            LastName = "Chari",
            PasswordHash = "x",
            Role = ApplicationRoles.TillOperator,
            IsActive = true,
            ShopId = shopId,
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<UserInfo> SignInInfoAsync()
    {
        var user = await _context.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == Operator);
        return await UserInfoMapper.FromUserAsync(user, _context);
    }

    /// <summary>The body KefShop's review screen posts: both ends named only when it knows them.</summary>
    private static CreateDesktopTransferRequestDto TillRequest(string? fromWarehouse) => new()
    {
        FromWarehouse = fromWarehouse,
        ToWarehouse = "KEFGRS",
        DocDate = "2026-09-14",
        Lines =
        [
            new CreateDesktopTransferRequestLineDto
            {
                ItemCode = "VHU002",
                Quantity = 1m,
                UoMCode = "EA",
                FromWarehouseCode = fromWarehouse,
                ToWarehouseCode = "KEFGRS",
            }
        ]
    };

    private Task<ErrorOr.ErrorOr<InventoryTransferRequestDto>> Handle(CreateDesktopTransferRequestDto request) =>
        new CreateTransferRequestHandler(
                _context,
                StubProxy.For<ISAPServiceLayerClient>((method, args) =>
                {
                    if (method.Name != nameof(ISAPServiceLayerClient.CreateInventoryTransferRequestAsync))
                    {
                        throw new InvalidOperationException(
                            $"ISAPServiceLayerClient.{method.Name} was not expected on this path.");
                    }

                    _posted = (CreateTransferRequestDto)args![0]!;
                    return Task.FromResult(new InventoryTransferRequest { DocEntry = 501, DocNum = 9001 });
                }),
                StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
                StubProxy.For<IInventoryTransferApprovalService>((_, _) =>
                    Task.FromResult(new ApprovalRequestEntity())),
                Options.Create(new SAPSettings { Enabled = true }),
                NullLogger<CreateTransferRequestHandler>.Instance)
            .Handle(new CreateTransferRequestCommand(request, Operator.ToString()), default);
}
