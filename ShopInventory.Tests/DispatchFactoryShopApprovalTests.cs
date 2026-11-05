using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers transfers that draw from, or deliver to, several warehouses at once — Dispatch's four
/// warehouses into the factory shop, and the shop's returns back to them — and the approval stages
/// limited to the stock officer at the receiving end.
/// </summary>
/// <remarks>
/// Two failures here would be quiet. A template matched on the header alone would let a line from
/// an unlisted warehouse ride through on another line's route. And a warehouse-scoped stage that
/// forgot the scope anywhere — the decision, the queue's Approve button, the alerts — would either
/// let any stock officer sign for the shop or offer a button the decision then refuses.
/// </remarks>
public sealed class DispatchFactoryShopApprovalTests : IDisposable
{
    private static readonly string[] Dispatch = ["DISPCH", "DISPYOG", "DISPMILK", "DISPICE"];
    private const string FactoryShop = "KEFSHOP";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly NotificationService _notifications;

    public DispatchFactoryShopApprovalTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _notifications = new NotificationService(
            _context,
            NullLogger<NotificationService>.Instance,
            new NoOpHubContext(),
            new StubPushNotificationService(),
            new RecordingWebhookService());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── Template matching ───────────────────────────────

    [Fact]
    public async Task A_transfer_from_several_dispatch_warehouses_routes_to_the_factory_shop_template()
    {
        var (_, toShop, _) = await SetUpRoutesAsync();
        var raiser = await AddUserAsync(ApplicationRoles.StockController, "DISPCH");
        var pending = await AddPendingTransferAsync(raiser, ("DISPCH", FactoryShop), ("DISPYOG", FactoryShop), ("DISPICE", FactoryShop));

        var request = await ApprovalService().EnsureRequestAsync(
            ApprovalDocumentContext.ForPendingTransfer(pending), raiser.Id, default);

        Assert.Equal(toShop.Id, request.ApprovalTemplateId);
        Assert.Equal("DISPCH, DISPYOG, DISPICE", request.FromWarehouse);
        Assert.Equal(FactoryShop, request.ToWarehouse);
    }

    [Fact]
    public async Task A_line_from_an_unlisted_warehouse_keeps_the_transfer_off_the_template()
    {
        // FSRW is not a dispatch warehouse. Matching on any one line would have let it through on
        // the strength of the DISPCH line beside it.
        var (_, toShop, _) = await SetUpRoutesAsync();
        var raiser = await AddUserAsync(ApplicationRoles.StockController, "DISPCH");
        var pending = await AddPendingTransferAsync(raiser, ("DISPCH", FactoryShop), ("FSRW", FactoryShop));

        var request = await ApprovalService().EnsureRequestAsync(
            ApprovalDocumentContext.ForPendingTransfer(pending), raiser.Id, default);

        Assert.NotEqual(toShop.Id, request.ApprovalTemplateId);
        Assert.Equal("General Direct Transfer Review", request.TemplateName);
    }

    [Fact]
    public async Task A_template_with_no_warehouses_on_an_end_matches_any_warehouse_there()
    {
        var service = ApprovalService();
        var stage = await SaveStageAsync(service, "Any officer", ApprovalWarehouseScopes.Any, perWarehouse: false);
        var anywhereToShop = await SaveTemplateAsync(service, "Anything into the shop", [], [FactoryShop], stage.Id);
        var raiser = await AddUserAsync(ApplicationRoles.StockController);
        var pending = await AddPendingTransferAsync(raiser, ("WAREH", FactoryShop));

        var request = await service.EnsureRequestAsync(ApprovalDocumentContext.ForPendingTransfer(pending), raiser.Id, default);

        Assert.Equal(anywhereToShop.Id, request.ApprovalTemplateId);
    }

    [Fact]
    public async Task Saved_warehouse_lists_read_back_trimmed_and_without_repeats()
    {
        var service = ApprovalService();
        var stage = await SaveStageAsync(service, "Any officer", ApprovalWarehouseScopes.Any, perWarehouse: false);

        await SaveTemplateAsync(service, "Listed", [" DISPCH ", "DISPCH", "dispch", "DISPYOG", ""], [FactoryShop], stage.Id);

        var stored = Assert.Single((await service.GetTemplatesAsync(default)), template => template.Name == "Listed");
        Assert.Equal(["DISPCH", "DISPYOG"], stored.FromWarehouses);
        Assert.Equal([FactoryShop], stored.ToWarehouses);
    }

    // ── The receiving stock officer ───────────────────────────────

    [Fact]
    public async Task The_factory_shops_stock_officer_approves_stock_coming_into_the_shop()
    {
        await SetUpRoutesAsync();
        var raiser = await AddUserAsync(ApplicationRoles.StockController, "DISPYOG");
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);
        var pending = await AddPendingTransferAsync(raiser, ("DISPYOG", FactoryShop), ("DISPCH", FactoryShop));

        var result = await ApprovalService().SubmitDecisionAsync(
            ApprovalDocumentContext.ForPendingTransfer(pending), shopOfficer.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.False(result.IsError);
        Assert.True(result.Value.ApprovalProcessComplete);
    }

    [Fact]
    public async Task A_stock_officer_at_another_warehouse_cannot_approve_for_the_shop()
    {
        await SetUpRoutesAsync();
        var raiser = await AddUserAsync(ApplicationRoles.StockController, "DISPYOG");
        var depotOfficer = await AddUserAsync(ApplicationRoles.StockController, "KEFGRC");
        var pending = await AddPendingTransferAsync(raiser, ("DISPYOG", FactoryShop));

        var result = await ApprovalService().SubmitDecisionAsync(
            ApprovalDocumentContext.ForPendingTransfer(pending), depotOfficer.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.True(result.IsError);
        Assert.Equal("ApprovalProcess.NotAuthorizer", result.FirstError.Code);
    }

    [Fact]
    public async Task The_queue_offers_approve_only_to_the_receiving_stock_officer()
    {
        await SetUpRoutesAsync();
        var raiser = await AddUserAsync(ApplicationRoles.StockController, "DISPYOG");
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);
        var depotOfficer = await AddUserAsync(ApplicationRoles.StockController, "KEFGRC");
        var pending = await AddPendingTransferAsync(raiser, ("DISPYOG", FactoryShop));
        var (_, stages) = await ApprovalService().GetProgressAsync(ApprovalDocumentContext.ForPendingTransfer(pending), default);
        var stage = Assert.Single(stages);

        Assert.Equal([FactoryShop], stage.ScopeWarehouses);
        Assert.True(InventoryTransferApprovalService.CanAdvance(shopOfficer, stage));
        Assert.False(InventoryTransferApprovalService.CanAdvance(depotOfficer, stage));
    }

    [Fact]
    public async Task A_scoped_stage_without_per_warehouse_counting_still_turns_away_other_warehouses()
    {
        // The per-warehouse check would turn an outsider away on its own; this stage has none, so
        // only the warehouse scope stands between a depot's stock officer and the shop's approval.
        var service = ApprovalService();
        var stage = await SaveStageAsync(service, "Shop officer", ApprovalWarehouseScopes.Destination, perWarehouse: false);
        await SaveTemplateAsync(service, "Into the shop", Dispatch, [FactoryShop], stage.Id);
        var raiser = await AddUserAsync(ApplicationRoles.StockController, "DISPYOG");
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);
        var depotOfficer = await AddUserAsync(ApplicationRoles.StockController, "KEFGRC");
        var pending = await AddPendingTransferAsync(raiser, ("DISPYOG", FactoryShop));

        var (_, stages) = await service.GetProgressAsync(ApprovalDocumentContext.ForPendingTransfer(pending), default);
        var progress = Assert.Single(stages);

        Assert.True(InventoryTransferApprovalService.CanAdvance(shopOfficer, progress));
        Assert.False(InventoryTransferApprovalService.CanAdvance(depotOfficer, progress));
    }

    [Fact]
    public async Task Only_the_receiving_stock_officer_is_alerted()
    {
        await SetUpRoutesAsync();
        var raiser = await AddUserAsync(ApplicationRoles.StockController, "DISPYOG");
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);
        await AddUserAsync(ApplicationRoles.StockController, "KEFGRC");
        var pending = await AddPendingTransferAsync(raiser, ("DISPYOG", FactoryShop));

        await ApprovalService().EnsureRequestAsync(ApprovalDocumentContext.ForPendingTransfer(pending), raiser.Id, default);

        var alerted = await _context.Notifications.AsNoTracking()
            .Where(item => item.EntityType == "TransferRequestApproval")
            .Select(item => item.TargetUsername)
            .ToListAsync();
        Assert.Equal([shopOfficer.Username], alerted);
    }

    // ── Returns to several dispatch warehouses ───────────────────────────────

    [Fact]
    public async Task A_return_to_two_dispatch_warehouses_needs_each_ones_stock_officer()
    {
        var (_, _, fromShop) = await SetUpRoutesAsync();
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);
        var yoghurtOfficer = await AddUserAsync(ApplicationRoles.StockController, "DISPYOG");
        var cheeseOfficer = await AddUserAsync(ApplicationRoles.StockController, "DISPCH");
        var pending = await AddPendingTransferAsync(shopOfficer, (FactoryShop, "DISPYOG"), (FactoryShop, "DISPCH"));
        var document = ApprovalDocumentContext.ForPendingTransfer(pending);
        var service = ApprovalService();
        var request = await service.EnsureRequestAsync(document, shopOfficer.Id, default);
        Assert.Equal(fromShop.Id, request.ApprovalTemplateId);

        var first = await service.SubmitDecisionAsync(document, yoghurtOfficer.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.False(first.IsError);
        Assert.False(first.Value.ApprovalProcessComplete);
        Assert.Contains("DISPCH", first.Value.Message);
        Assert.DoesNotContain("DISPYOG", first.Value.Message);

        StartNewRequestScope();
        var second = await ApprovalService().SubmitDecisionAsync(document, cheeseOfficer.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.False(second.IsError);
        Assert.True(second.Value.ApprovalProcessComplete);
    }

    [Fact]
    public async Task One_dispatch_warehouse_approving_twice_does_not_stand_in_for_the_other()
    {
        await SetUpRoutesAsync();
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);
        var yoghurtOfficer = await AddUserAsync(ApplicationRoles.StockController, "DISPYOG");
        var secondYoghurtOfficer = await AddUserAsync(ApplicationRoles.StockController, "DISPYOG");
        var pending = await AddPendingTransferAsync(shopOfficer, (FactoryShop, "DISPYOG"), (FactoryShop, "DISPCH"));
        var document = ApprovalDocumentContext.ForPendingTransfer(pending);

        await ApprovalService().SubmitDecisionAsync(document, yoghurtOfficer.Id, ApprovalDecisionValues.Approved, null, null, default);
        var result = await ApprovalService().SubmitDecisionAsync(document, secondYoghurtOfficer.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.False(result.IsError);
        Assert.False(result.Value.ApprovalProcessComplete);
        var (_, stages) = await ApprovalService().GetProgressAsync(document, default);
        var stage = Assert.Single(stages);
        Assert.Equal(ApprovalRequestStatuses.Pending, stage.Status);
        Assert.Equal(1, stage.ApprovalCount);
        Assert.Equal(2, stage.ApprovalsRequired);
        Assert.Equal(ApprovalRequestStatuses.Approved, stage.WarehouseProgress.Single(item => item.WarehouseCode == "DISPYOG").Status);
        Assert.Equal(ApprovalRequestStatuses.Pending, stage.WarehouseProgress.Single(item => item.WarehouseCode == "DISPCH").Status);
    }

    [Fact]
    public async Task The_warehouses_a_decision_speaks_for_are_stored_with_it()
    {
        await SetUpRoutesAsync();
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);
        var bothOfficer = await AddUserAsync(ApplicationRoles.StockController, "DISPYOG", "DISPCH", "KEFGRC");
        var pending = await AddPendingTransferAsync(shopOfficer, (FactoryShop, "DISPYOG"), (FactoryShop, "DISPCH"));
        var document = ApprovalDocumentContext.ForPendingTransfer(pending);
        await ApprovalService().EnsureRequestAsync(document, shopOfficer.Id, default);
        StartNewRequestScope();

        var result = await ApprovalService().SubmitDecisionAsync(document, bothOfficer.Id, ApprovalDecisionValues.Approved, null, null, default);

        // Assigned to both destinations, so one approval covers both; KEFGRC is not on the transfer.
        Assert.True(result.Value.ApprovalProcessComplete);
        var stored = await _context.ApprovalDecisions.AsNoTracking().SingleAsync();
        Assert.Equal("[\"DISPYOG\",\"DISPCH\"]", stored.WarehouseCodesJson);
    }

    [Fact]
    public async Task An_administrator_approves_for_every_receiving_warehouse()
    {
        await SetUpRoutesAsync();
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var pending = await AddPendingTransferAsync(shopOfficer, (FactoryShop, "DISPYOG"), (FactoryShop, "DISPCH"));

        var result = await ApprovalService().SubmitDecisionAsync(
            ApprovalDocumentContext.ForPendingTransfer(pending), admin.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.False(result.IsError);
        Assert.True(result.Value.ApprovalProcessComplete);
    }

    [Fact]
    public async Task One_rejection_from_either_warehouse_stops_the_return()
    {
        await SetUpRoutesAsync();
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);
        var yoghurtOfficer = await AddUserAsync(ApplicationRoles.StockController, "DISPYOG");
        var pending = await AddPendingTransferAsync(shopOfficer, (FactoryShop, "DISPYOG"), (FactoryShop, "DISPCH"));

        var result = await ApprovalService().SubmitDecisionAsync(
            ApprovalDocumentContext.ForPendingTransfer(pending), yoghurtOfficer.Id, ApprovalDecisionValues.NotApproved, null, "Not ours", default);

        Assert.True(result.Value.Rejected);
    }

    // ── Preview, stages and the held transfer's route ───────────────────────────────

    [Fact]
    public async Task The_preview_names_a_receiving_warehouse_nobody_is_assigned_to()
    {
        await SetUpRoutesAsync();
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);
        await AddUserAsync(ApplicationRoles.StockController, "DISPYOG");

        var preview = await ApprovalService().PreviewRouteAsync(
            ApprovalDocumentContext.ForPreview(ApprovalDocumentTypes.InventoryTransfer, [FactoryShop], ["DISPYOG", "DISPICE"], shopOfficer.Id),
            default);

        Assert.True(preview.Matched);
        Assert.Equal("Factory Shop returns to Dispatch", preview.TemplateName);
        var stage = Assert.Single(preview.Stages);
        Assert.Equal(["DISPYOG", "DISPICE"], stage.ScopeWarehouses);
        Assert.Equal(["DISPICE"], stage.WarehousesWithoutAuthorizer);
        Assert.Equal(1, stage.AuthorizerCount);
    }

    [Fact]
    public async Task The_preview_does_not_count_the_raiser_as_an_approver()
    {
        // The shop's own officer cannot approve a transfer they raised, so a preview that counted
        // them would promise an approver who will never be able to act.
        await SetUpRoutesAsync();
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);

        var preview = await ApprovalService().PreviewRouteAsync(
            ApprovalDocumentContext.ForPreview(ApprovalDocumentTypes.InventoryTransfer, ["DISPYOG"], [FactoryShop], shopOfficer.Id),
            default);

        var stage = Assert.Single(preview.Stages);
        Assert.Equal(0, stage.AuthorizerCount);
        Assert.Equal([FactoryShop], stage.WarehousesWithoutAuthorizer);
    }

    [Fact]
    public async Task An_unscoped_stage_cannot_be_saved_as_per_warehouse()
    {
        var stage = await SaveStageAsync(ApprovalService(), "Loose", ApprovalWarehouseScopes.Any, perWarehouse: true);

        Assert.False(stage.ApprovalPerWarehouse);
        Assert.Equal(ApprovalWarehouseScopes.Any, stage.WarehouseScope);
    }

    [Fact]
    public async Task A_held_transfer_lists_every_source_warehouse_without_its_lines()
    {
        var raiser = await AddUserAsync(ApplicationRoles.StockController);
        var pending = await AddPendingTransferAsync(raiser, ("DISPCH", FactoryShop), ("DISPYOG", FactoryShop), ("DISPCH", FactoryShop));

        var dto = PendingInventoryTransferMapper.ToDto(pending, includeLines: false);

        Assert.Equal(["DISPCH", "DISPYOG"], dto.FromWarehouses);
        Assert.Equal([FactoryShop], dto.ToWarehouses);
        Assert.Empty(dto.Lines);
    }

    [Fact]
    public async Task Reopening_a_changed_return_re_aims_the_stage_at_its_new_warehouses()
    {
        await SetUpRoutesAsync();
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, FactoryShop);
        var pending = await AddPendingTransferAsync(shopOfficer, (FactoryShop, "DISPYOG"));
        await ApprovalService().EnsureRequestAsync(ApprovalDocumentContext.ForPendingTransfer(pending), shopOfficer.Id, default);

        pending.PayloadJson = Payload((FactoryShop, "DISPYOG"), (FactoryShop, "DISPMILK"));
        await _context.SaveChangesAsync();
        StartNewRequestScope();
        await ApprovalService().ReopenAsync(ApprovalDocumentContext.ForPendingTransfer(pending), shopOfficer.Id, default);

        var (request, stages) = await ApprovalService().GetProgressAsync(ApprovalDocumentContext.ForPendingTransfer(pending), default);
        Assert.Equal(["DISPYOG", "DISPMILK"], Assert.Single(stages).ScopeWarehouses);
        Assert.Equal("DISPYOG, DISPMILK", request.ToWarehouse);
    }

    // ── Helpers ───────────────────────────────

    /// <summary>The receiving-officer stage and the two templates the factory shop runs on.</summary>
    private async Task<(ApprovalStageDefinitionDto Stage, ApprovalTemplateDefinitionDto ToShop, ApprovalTemplateDefinitionDto FromShop)> SetUpRoutesAsync()
    {
        var service = ApprovalService();
        var stage = await SaveStageAsync(service, "Receiving stock officer", ApprovalWarehouseScopes.Destination, perWarehouse: true);
        var toShop = await SaveTemplateAsync(service, "Dispatch to Factory Shop", Dispatch, [FactoryShop], stage.Id);
        var fromShop = await SaveTemplateAsync(service, "Factory Shop returns to Dispatch", [FactoryShop], Dispatch, stage.Id);
        StartNewRequestScope();
        return (stage, toShop, fromShop);
    }

    private static async Task<ApprovalStageDefinitionDto> SaveStageAsync(
        IInventoryTransferApprovalService service, string name, string scope, bool perWarehouse)
    {
        var saved = await service.SaveStageAsync(new ApprovalStageDefinitionDto
        {
            Name = name,
            AuthorizerRoles = [ApplicationRoles.StockController],
            WarehouseScope = scope,
            ApprovalPerWarehouse = perWarehouse
        }, default);
        Assert.False(saved.IsError);
        return saved.Value;
    }

    private static async Task<ApprovalTemplateDefinitionDto> SaveTemplateAsync(
        IInventoryTransferApprovalService service, string name, IEnumerable<string> from, IEnumerable<string> to, Guid stageId)
    {
        var saved = await service.SaveTemplateAsync(new ApprovalTemplateDefinitionDto
        {
            Name = name,
            DocumentType = ApprovalDocumentTypes.InventoryTransfer,
            StageIds = [stageId],
            FromWarehouses = [.. from],
            ToWarehouses = [.. to],
            Priority = 200
        }, default);
        Assert.False(saved.IsError);
        return saved.Value;
    }

    /// <summary>Stands in for the next HTTP request, which loads everything from storage again.</summary>
    private void StartNewRequestScope() => _context.ChangeTracker.Clear();

    private IInventoryTransferApprovalService ApprovalService() => new InventoryTransferApprovalService(
        _context,
        _notifications,
        NullLogger<InventoryTransferApprovalService>.Instance);

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

    private async Task<PendingInventoryTransferEntity> AddPendingTransferAsync(
        User submitter, params (string From, string To)[] lines)
    {
        var pending = new PendingInventoryTransferEntity
        {
            FromWarehouse = lines[0].From,
            ToWarehouse = lines[0].To,
            PayloadJson = Payload(lines),
            CreatedByUserId = submitter.Id,
            CreatedByName = submitter.Username,
            CreatedByRole = submitter.Role,
            LineCount = lines.Length,
            TotalQuantity = lines.Length
        };
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();
        return pending;
    }

    private static string Payload(params (string From, string To)[] lines)
        => PendingInventoryTransferMapper.SerializePayload(new CreateInventoryTransferRequest
        {
            FromWarehouse = lines[0].From,
            ToWarehouse = lines[0].To,
            Lines = lines.Select((line, index) => new CreateInventoryTransferLineRequest
            {
                ItemCode = $"ITEM{index:000}",
                Quantity = 1,
                FromWarehouseCode = line.From,
                ToWarehouseCode = line.To
            }).ToList()
        });
}
