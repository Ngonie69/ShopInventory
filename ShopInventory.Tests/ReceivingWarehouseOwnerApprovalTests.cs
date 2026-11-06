using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers;
using ShopInventory.Features.InventoryTransfers.Commands.CloseTransferRequest;
using ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A transfer request waits for the owner of each warehouse it delivers to, except vans, and except a
/// warehouse the requester runs, whose request is its own agreement.
/// </summary>
/// <remarks>
/// The quiet failures here are the ones that ask the wrong people, or the right people twice: a
/// depot's controller asked once as "Depot Acceptance" and again as the depot's owner; a van rep asked
/// to approve their own load; an administrator's review read as a warehouse's agreement; a factory
/// officer let in to approve who could then post or close what they only had a say in.
/// </remarks>
public sealed class ReceivingWarehouseOwnerApprovalTests : IDisposable
{
    private const string Depot = "KEFGRC";
    private const string OtherDepot = "KEFBYC";
    private const string Shop = "KEFSHOP";
    private const string Van = "VAN010";
    private const string Warehouse = "WAREH";
    private const string Dispatch = "DISPCH";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly NotificationService _notifications;
    private int _nextDocEntry = 9000;

    public ReceivingWarehouseOwnerApprovalTests()
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

    // ── Who has to agree ───────────────────────────────

    [Fact]
    public async Task A_request_into_a_depot_waits_for_the_depots_owner()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var document = Request((Warehouse, Depot));

        var (request, stages) = await OpenAsync(document, officer);

        var receiving = Assert.Single(stages, stage => stage.StageId == ReceivingWarehouseOwnerRule.StageId);
        Assert.Equal([Depot], receiving.ScopeWarehouses);
        Assert.Equal(ApprovalRequestStatuses.Pending, receiving.Status);
        Assert.Equal(ApprovalRequestStatuses.Pending, request.Status);
    }

    [Fact]
    public async Task A_van_stock_request_has_no_receiving_owner_stage()
    {
        // The rep who drives the van is the one asking for the stock.
        var rep = await AddUserAsync(ApplicationRoles.Sales, Van);

        var (_, stages) = await OpenAsync(Request((Depot, Van)), rep);

        Assert.DoesNotContain(stages, stage => stage.StageId == ReceivingWarehouseOwnerRule.StageId);
    }

    [Fact]
    public async Task Only_the_destinations_that_are_not_vans_wait()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);

        var (_, stages) = await OpenAsync(Request((Depot, Van), (Depot, Shop)), officer);

        var receiving = Assert.Single(stages, stage => stage.StageId == ReceivingWarehouseOwnerRule.StageId);
        Assert.Equal([Shop], receiving.ScopeWarehouses);
    }

    [Theory]
    [InlineData("VAN001", true)]
    [InlineData("van019", true)]
    [InlineData("VAN021", true)]
    [InlineData("VANSALES", false)]
    [InlineData("KEFVAN", false)]
    [InlineData("VAN", false)]
    [InlineData(null, false)]
    public void Vans_are_recognised_by_their_code(string? code, bool isVan)
        => Assert.Equal(isVan, ReceivingWarehouseOwnerRule.IsVanWarehouse(code));

    // ── The requester's own warehouse ───────────────────────────────

    [Fact]
    public async Task A_request_raised_by_the_receiving_warehouses_owner_is_its_own_agreement()
    {
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Depot);

        var (request, stages) = await OpenAsync(Request((Dispatch, Depot)), controller);

        var receiving = Assert.Single(stages, stage => stage.StageId == ReceivingWarehouseOwnerRule.StageId);
        Assert.Equal(ApprovalRequestStatuses.Approved, receiving.Status);
        var depot = Assert.Single(receiving.WarehouseProgress);
        Assert.True(depot.ConsentedByRequester);
        Assert.Empty(receiving.Decisions);
        // The template's own stage still stands: their agreement is not the stock officer's approval.
        Assert.Equal(ApprovalRequestStatuses.Pending, request.Status);
    }

    [Fact]
    public async Task A_requester_who_runs_one_of_two_destinations_still_waits_for_the_other()
    {
        var shopOfficer = await AddUserAsync(ApplicationRoles.StockController, Shop);

        var (_, stages) = await OpenAsync(Request((Warehouse, Shop), (Warehouse, Depot)), shopOfficer);

        var receiving = Assert.Single(stages, stage => stage.StageId == ReceivingWarehouseOwnerRule.StageId);
        Assert.Equal(ApprovalRequestStatuses.Pending, receiving.Status);
        Assert.True(receiving.WarehouseProgress.Single(item => item.WarehouseCode == Shop).ConsentedByRequester);
        Assert.Equal(ApprovalRequestStatuses.Pending, receiving.WarehouseProgress.Single(item => item.WarehouseCode == Depot).Status);
    }

    [Fact]
    public async Task Raising_a_request_as_an_administrator_signs_for_no_warehouse()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin, Depot);

        var (_, stages) = await OpenAsync(Request((Warehouse, Depot)), admin);

        var receiving = Assert.Single(stages, stage => stage.StageId == ReceivingWarehouseOwnerRule.StageId);
        Assert.Equal(ApprovalRequestStatuses.Pending, receiving.Status);
    }

    // ── Deciding ───────────────────────────────

    [Fact]
    public async Task A_dispatch_officer_approves_stock_coming_into_their_warehouse()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var dispatchOfficer = await AddUserAsync(ApplicationRoles.DispatchOfficer, Dispatch);
        var document = Request((Warehouse, Dispatch));
        await OpenAsync(document, officer);

        var result = await ApprovalService().SubmitDecisionAsync(
            document, dispatchOfficer.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.False(result.IsError, string.Join("; ", result.ErrorsOrEmptyList.Select(error => error.Description)));
        Assert.Equal(ReceivingWarehouseOwnerRule.StageName, result.Value.StageName);
    }

    [Fact]
    public async Task The_owner_of_another_warehouse_cannot_approve()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var otherDispatch = await AddUserAsync(ApplicationRoles.DispatchOfficer, "DISPYOG");
        var document = Request((Warehouse, Dispatch));
        await OpenAsync(document, officer);

        var result = await ApprovalService().SubmitDecisionAsync(
            document, otherDispatch.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.True(result.IsError);
        Assert.Equal("ApprovalProcess.NotAuthorizer", result.FirstError.Code);
    }

    [Fact]
    public async Task The_depots_controller_approves_once_for_both_of_their_stages()
    {
        // A stock officer's request routes to "Depot Acceptance", which the depot's controller holds;
        // they are also the depot's owner. One approval is one judgement and counts for both.
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Depot);
        var document = Request((Warehouse, Depot));
        await OpenAsync(document, officer);

        var result = await ApprovalService().SubmitDecisionAsync(
            document, controller.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.False(result.IsError);
        Assert.Equal("Depot Acceptance", result.Value.StageName);
        Assert.True(result.Value.ApprovalProcessComplete);
        var agreed = Assert.Single(await _context.ApprovalDecisions.AsNoTracking()
            .Where(decision => decision.StageId == ReceivingWarehouseOwnerRule.StageId).ToListAsync());
        Assert.Equal(controller.Id, agreed.AuthorizerUserId);
        Assert.Contains("Depot Acceptance", agreed.Remarks);
        Assert.Equal($"[\"{Depot}\"]", agreed.WarehouseCodesJson);
    }

    [Fact]
    public async Task Another_depots_controller_accepting_does_not_sign_for_the_depot()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var elsewhere = await AddUserAsync(ApplicationRoles.DepotController, OtherDepot);
        var document = Request((Warehouse, Depot));
        await OpenAsync(document, officer);

        var result = await ApprovalService().SubmitDecisionAsync(
            document, elsewhere.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.False(result.IsError);
        Assert.Equal("Depot Acceptance", result.Value.StageName);
        Assert.False(result.Value.ApprovalProcessComplete);
        _context.ChangeTracker.Clear();
        var (_, stages) = await ApprovalService().GetProgressAsync(ApprovalDocumentContext.ForTransferRequest(document), default);
        var receiving = Assert.Single(stages, stage => stage.StageId == ReceivingWarehouseOwnerRule.StageId);
        Assert.Equal(ApprovalRequestStatuses.Pending, receiving.Status);
        Assert.Empty(receiving.Decisions);
    }

    [Fact]
    public async Task An_administrators_review_never_signs_for_a_warehouse()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var document = Request((Warehouse, Depot)); // raised in SAP: nobody on file

        var result = await ApprovalService().SubmitDecisionAsync(
            document, admin.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.Equal("Administrator Review", result.Value.StageName);
        Assert.False(result.Value.ApprovalProcessComplete);
        Assert.Empty(await _context.ApprovalDecisions.AsNoTracking()
            .Where(decision => decision.StageId == ReceivingWarehouseOwnerRule.StageId).ToListAsync());
    }

    // ── Alerts ───────────────────────────────

    [Fact]
    public async Task The_depots_controller_is_alerted_once()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Depot);

        await OpenAsync(Request((Warehouse, Depot)), officer);

        var alerted = await Alerted();
        Assert.Equal([controller.Username], alerted);
    }

    [Fact]
    public async Task An_owner_who_holds_no_other_stage_is_alerted_for_the_receiving_stage()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var dispatchOfficer = await AddUserAsync(ApplicationRoles.DispatchOfficer, Dispatch);
        await AddUserAsync(ApplicationRoles.DispatchOfficer, "DISPYOG");

        await OpenAsync(Request((Warehouse, Dispatch)), officer);

        Assert.Contains(dispatchOfficer.Username, await Alerted());
        Assert.Single(await Alerted(), name => name.StartsWith("dispatchofficer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_requester_who_runs_the_destination_alerts_nobody_as_its_owner()
    {
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Depot);
        var colleague = await AddUserAsync(ApplicationRoles.DepotController, Depot);

        await OpenAsync(Request((Dispatch, Depot)), controller);

        var receivingAlerts = await _context.Notifications.AsNoTracking()
            .Where(item => item.EntityType == "TransferRequestApproval" &&
                           item.EntityId!.EndsWith(ReceivingWarehouseOwnerRule.StageId.ToString("N")))
            .ToListAsync();
        Assert.Empty(receivingAlerts);
        Assert.DoesNotContain(colleague.Username, await Alerted());
    }

    // ── Configuration and other documents ───────────────────────────────

    [Fact]
    public async Task Switching_the_stage_off_turns_the_rule_off()
    {
        var service = ApprovalService();
        var stage = (await service.GetStagesAsync(default)).Single(item => item.Id == ReceivingWarehouseOwnerRule.StageId);
        stage.IsActive = false;
        Assert.False((await service.SaveStageAsync(stage, default)).IsError);
        _context.ChangeTracker.Clear();
        var officer = await AddUserAsync(ApplicationRoles.StockController);

        var (_, stages) = await OpenAsync(Request((Warehouse, Depot)), officer);

        Assert.DoesNotContain(stages, item => item.StageId == ReceivingWarehouseOwnerRule.StageId);
    }

    [Fact]
    public async Task The_seeded_stage_names_the_warehouse_roles()
    {
        var stage = (await ApprovalService().GetStagesAsync(default))
            .Single(item => item.Id == ReceivingWarehouseOwnerRule.StageId);

        Assert.Equal(ReceivingWarehouseOwnerRule.StageName, stage.Name);
        Assert.Equal(ApprovalWarehouseScopes.Destination, stage.WarehouseScope);
        Assert.True(stage.ApprovalPerWarehouse);
        Assert.Equal(ReceivingWarehouseOwnerRule.DefaultRoles, stage.AuthorizerRoles);
    }

    [Fact]
    public async Task Direct_transfers_are_left_to_their_templates()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var pending = new PendingInventoryTransferEntity
        {
            FromWarehouse = Warehouse,
            ToWarehouse = Depot,
            PayloadJson = PendingInventoryTransferMapper.SerializePayload(new CreateInventoryTransferRequest
            {
                FromWarehouse = Warehouse,
                ToWarehouse = Depot,
                Lines = [new CreateInventoryTransferLineRequest { ItemCode = "ITEM1", Quantity = 1, FromWarehouseCode = Warehouse, ToWarehouseCode = Depot }]
            }),
            CreatedByUserId = officer.Id,
            CreatedByName = officer.Username,
            CreatedByRole = officer.Role,
            LineCount = 1,
            TotalQuantity = 1
        };
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();

        var (_, stages) = await ApprovalService().GetProgressAsync(ApprovalDocumentContext.ForPendingTransfer(pending), default);

        Assert.DoesNotContain(stages, stage => stage.StageId == ReceivingWarehouseOwnerRule.StageId);
    }

    [Fact]
    public async Task Reopening_a_changed_request_re_aims_the_receiving_stage_and_keeps_the_requesters_agreement()
    {
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Depot);
        var before = Request((Dispatch, Depot));
        await OpenAsync(before, controller);

        var after = Request((Dispatch, Depot), (Dispatch, Shop), (Dispatch, Van));
        after.DocEntry = before.DocEntry;
        after.DocNum = before.DocNum;
        _context.ChangeTracker.Clear();
        await ApprovalService().ReopenAsync(ApprovalDocumentContext.ForTransferRequest(after), controller.Id, default);

        _context.ChangeTracker.Clear();
        var (_, stages) = await ApprovalService().GetProgressAsync(ApprovalDocumentContext.ForTransferRequest(after), default);
        var receiving = Assert.Single(stages, stage => stage.StageId == ReceivingWarehouseOwnerRule.StageId);
        Assert.Equal([Depot, Shop], receiving.ScopeWarehouses);
        Assert.True(receiving.WarehouseProgress.Single(item => item.WarehouseCode == Depot).ConsentedByRequester);
        Assert.Equal(ApprovalRequestStatuses.Pending, receiving.WarehouseProgress.Single(item => item.WarehouseCode == Shop).Status);
    }

    [Fact]
    public async Task The_preview_shows_the_receiving_stage_and_who_can_answer_it()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController, Shop);
        await AddUserAsync(ApplicationRoles.DepotController, Depot);

        var preview = await ApprovalService().PreviewRouteAsync(
            ApprovalDocumentContext.ForPreview(ApprovalDocumentTypes.InventoryTransferRequest, [Warehouse], [Shop, Depot, Van, OtherDepot], officer.Id),
            default);

        var receiving = Assert.Single(preview.Stages, stage => stage.StageId == ReceivingWarehouseOwnerRule.StageId);
        Assert.Equal([Shop, Depot, OtherDepot], receiving.ScopeWarehouses);
        // The shop is the raiser's own, and the depot has a controller; only KEFBYC has nobody.
        Assert.Equal([OtherDepot], receiving.WarehousesWithoutAuthorizer);
    }

    // ── Through the handlers ───────────────────────────────

    [Fact]
    public async Task A_factory_officer_approves_a_request_into_their_warehouse_but_cannot_post_it()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var dispatchOfficer = await AddUserAsync(ApplicationRoles.DispatchOfficer, Dispatch);
        var document = Request((Warehouse, Dispatch));
        await OpenAsync(document, officer);
        var sap = new SapRequests(document);

        var posting = await ConvertHandler(sap).Handle(
            new ConvertTransferRequestCommand(document.DocEntry, dispatchOfficer.Id, GenerateDocument: true), default);
        var approving = await ConvertHandler(sap).Handle(
            new ConvertTransferRequestCommand(document.DocEntry, dispatchOfficer.Id, GenerateDocument: false), default);

        Assert.True(posting.IsError);
        Assert.Equal("InventoryTransfer.TransferRequestConverterRoleRequired", posting.FirstError.Code);
        Assert.False(approving.IsError, string.Join("; ", approving.ErrorsOrEmptyList.Select(error => error.Description)));
        Assert.Null(approving.Value.Transfer);
        Assert.Equal(0, sap.Conversions);
    }

    [Fact]
    public async Task The_receiving_depots_controller_can_turn_the_request_down()
    {
        // They run the other end, not the source. Approving never asked about the source; refusing
        // used to, so the depot could agree to stock it could not refuse.
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Depot);
        var document = Request((Warehouse, Depot));
        await OpenAsync(document, officer);
        var sap = new SapRequests(document);

        var result = await CloseHandler(sap).Handle(
            new CloseTransferRequestCommand(document.DocEntry, controller.Id, Remarks: "No space"), default);

        Assert.False(result.IsError, string.Join("; ", result.ErrorsOrEmptyList.Select(error => error.Description)));
        Assert.Equal(1, sap.Closes);
    }

    [Fact]
    public async Task A_factory_officer_cannot_close_a_request_that_has_no_approval()
    {
        // A request raised in SAP has nothing to decide; closing it is the whole action, and that
        // stays with whoever could convert it.
        var dispatchOfficer = await AddUserAsync(ApplicationRoles.DispatchOfficer, Dispatch);
        var document = Request((Warehouse, Dispatch));
        var sap = new SapRequests(document);

        var result = await CloseHandler(sap).Handle(
            new CloseTransferRequestCommand(document.DocEntry, dispatchOfficer.Id), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.TransferRequestConverterRoleRequired", result.FirstError.Code);
        Assert.Equal(0, sap.Closes);
    }

    // ── Helpers ───────────────────────────────

    private InventoryTransferRequest Request(params (string From, string To)[] lines)
    {
        var docEntry = _nextDocEntry++;
        return new InventoryTransferRequest
        {
            DocEntry = docEntry,
            DocNum = docEntry,
            DocumentStatus = "bost_Open",
            FromWarehouse = lines[0].From,
            ToWarehouse = lines[0].To,
            StockTransferLines = lines.Select((line, index) => new InventoryTransferRequestLine
            {
                LineNum = index,
                ItemCode = $"ITEM{index:000}",
                Quantity = 1,
                FromWarehouseCode = line.From,
                WarehouseCode = line.To
            }).ToList()
        };
    }

    private async Task<(ApprovalRequestEntity Request, List<ApprovalStageProgressDto> Stages)> OpenAsync(
        InventoryTransferRequest document, User requester)
    {
        await ApprovalService().EnsureRequestAsync(document, requester.Id, default);
        _context.ChangeTracker.Clear();
        return await ApprovalService().GetProgressAsync(ApprovalDocumentContext.ForTransferRequest(document), default);
    }

    private Task<List<string>> Alerted() => _context.Notifications.AsNoTracking()
        .Where(item => item.EntityType == "TransferRequestApproval")
        .Select(item => item.TargetUsername!)
        .ToListAsync();

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

    private ConvertTransferRequestHandler ConvertHandler(SapRequests sap) => new(
        sap.AsClient(), ApprovalService(), new TransferWarehouseAuthorizer(_context),
        new AcquiringStore(), new NoOpAuditService(), new NoOpNotificationService(),
        StubProxy.Unused<IStockValidationService>(),
        new UnpostedTillClaims(_context, Options.Create(new DailyStockSettings()), NullLogger<UnpostedTillClaims>.Instance),
        Options.Create(new SAPSettings { Enabled = true }),
        NullLogger<ConvertTransferRequestHandler>.Instance);

    private CloseTransferRequestHandler CloseHandler(SapRequests sap) => new(
        sap.AsClient(), ApprovalService(), new TransferWarehouseAuthorizer(_context),
        new AcquiringStore(), new NoOpAuditService(),
        Options.Create(new SAPSettings { Enabled = true }),
        NullLogger<CloseTransferRequestHandler>.Instance);

    /// <summary>Serves one request from "SAP" and counts what was done to it there.</summary>
    private sealed class SapRequests(InventoryTransferRequest document)
    {
        public int Conversions { get; private set; }
        public int Closes { get; private set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetInventoryTransferRequestByDocEntryAsync) =>
                    Task.FromResult<InventoryTransferRequest?>(document),
                nameof(ISAPServiceLayerClient.CloseInventoryTransferRequestAsync) => Close(),
                nameof(ISAPServiceLayerClient.ConvertTransferRequestToTransferAsync) => Convert(),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task Close()
        {
            Closes++;
            return Task.CompletedTask;
        }

        private Task<InventoryTransfer> Convert()
        {
            Conversions++;
            return Task.FromResult(new InventoryTransfer { DocEntry = 1, DocNum = 1 });
        }
    }

    private sealed class AcquiringStore : IIdempotencyRequestStore
    {
        private long _nextId = 1;

        public Task<IdempotencyAcquireResult<TResponse>> TryAcquireAsync<TResponse>(
            string scope, string key, object request, CancellationToken cancellationToken)
            => Task.FromResult(new IdempotencyAcquireResult<TResponse>(IdempotencyAcquireOutcome.Acquired, _nextId++));

        public Task<IdempotencyAcquireResult<TResponse>> TryAcquireAsync<TResponse>(
            string scope, string key, object request, TimeSpan? inProgressLease, CancellationToken cancellationToken)
            => TryAcquireAsync<TResponse>(scope, key, request, cancellationToken);

        public Task<bool> RenewAsync(long requestId, TimeSpan lease, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task CompleteAsync<TResponse>(long requestId, TResponse response, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReleaseAsync(long requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryTakeOverAsync(long requestId, DateTime issuedBeforeUtc, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) => Task.CompletedTask;
    }
}
