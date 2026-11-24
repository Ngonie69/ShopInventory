using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Features.SapTransferApprovals;
using ShopInventory.Features.SapTransferApprovals.Commands.DecideSapTransferApproval;
using ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApproval;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Approving and rejecting a transfer that waits for SAP's approval, from SAP-Held Transfers. Shaped on
/// production as read on 2026-11-23: draft entry 89165 (PROYG 2 → WASHBAY), request 98635 on template 91
/// "Washbay Transfers", waiting at stage 44 'Factory Washbay', where SAP lists Software (key 75) — the user
/// the app decides as.
/// </summary>
public sealed class SapTransferDecisionTests : IDisposable
{
    private const int WashbayStage = 44;
    private const int OtherStage = 39;
    private const int ServiceApproverKey = 75;

    private static readonly SAPUser Software = new() { InternalKey = ServiceApproverKey, UserCode = "Software", UserName = "Developer2" };

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;

    public SapTransferDecisionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void The_waiting_view_asks_SAP_for_pending_drafts_and_the_default_for_approved_ones()
    {
        var waiting = SAPServiceLayerClient.ApprovedDraftQueueFilter(null, new SapTransferDraftFilter { AwaitingApproval = true });
        var approved = SAPServiceLayerClient.ApprovedDraftQueueFilter(null, new SapTransferDraftFilter());

        Assert.Contains("AuthorizationStatus eq 'sasPending'", waiting);
        Assert.DoesNotContain("sasApproved", waiting);
        Assert.Contains("AuthorizationStatus eq 'sasApproved'", approved);
        Assert.Contains("DocumentStatus eq 'bost_Open'", waiting);
    }

    [Fact]
    public void The_wash_bay_shop_stock_and_administrators_approve_SAP_held_transfers_by_default()
    {
        foreach (var role in new[] { ApplicationRoles.WashBay, ApplicationRoles.ShopStock, ApplicationRoles.Admin })
        {
            Assert.Contains(Permission.ApproveSapTransfers, Permission.GetDefaultPermissionsForRole(role));
        }

        foreach (var role in new[] { ApplicationRoles.StockController, ApplicationRoles.DepotController, ApplicationRoles.Manager })
        {
            Assert.DoesNotContain(Permission.ApproveSapTransfers, Permission.GetDefaultPermissionsForRole(role));
        }
    }

    /// <summary>
    /// A shop's stock user decides a transfer coming into its shop. No SAP stage is configured for the role,
    /// so its warehouses are the scope, with SAP's own listing of the app's approver on the stage.
    /// </summary>
    [Fact]
    public async Task A_shop_stock_user_approves_a_transfer_into_its_shop_and_not_one_touching_none_of_its_warehouses()
    {
        var shop = await UserAsync(ApplicationRoles.ShopStock, "KEFSHOP");

        var foreign = WaitingTransfer(WashbayStage);
        var refused = await Handler(foreign, scope: FixedStageScope.EveryStage).Handle(
            new DecideSapTransferApprovalCommand(98635, ApprovalDecisionValues.Approved, null, shop.Id, "shop", null),
            CancellationToken.None);
        Assert.Equal("SapTransferApproval.OutsideWarehouseScope", refused.FirstError.Code);
        Assert.Empty(foreign.Decisions);

        var incoming = WaitingTransfer(WashbayStage, from: "DISPCH", to: "KEFSHOP");
        var result = await Handler(incoming, scope: FixedStageScope.EveryStage).Handle(
            new DecideSapTransferApprovalCommand(98635, ApprovalDecisionValues.Approved, null, shop.Id, "shop", null),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Single(incoming.Decisions);
        Assert.True(result.Value.CanAdd);
    }

    [Fact]
    public async Task The_wash_bay_cannot_decide_a_transfer_touching_no_wash_bay_warehouse()
    {
        var washbay = await UserAsync(ApplicationRoles.WashBay);
        var sap = WaitingTransfer(WashbayStage, from: "PROYG 2", to: "DISPYOG");

        var result = await Handler(sap).Handle(
            new DecideSapTransferApprovalCommand(98635, ApprovalDecisionValues.Approved, null, washbay.Id, "washbay", null),
            CancellationToken.None);

        Assert.Equal("SapTransferApproval.OutsideWarehouseScope", result.FirstError.Code);
        Assert.Empty(sap.Decisions);
    }

    [Fact]
    public async Task The_wash_bay_approves_a_transfer_waiting_at_its_stage_and_it_becomes_ready_to_add()
    {
        var washbay = await UserAsync(ApplicationRoles.WashBay);
        var sap = WaitingTransfer(WashbayStage);
        var audit = new RecordingAuditService();

        var result = await Handler(sap, audit).Handle(
            new DecideSapTransferApprovalCommand(98635, ApprovalDecisionValues.Approved, "Crates counted", washbay.Id, "washbay", null),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var decision = Assert.Single(sap.Decisions);
        Assert.Equal(98635, decision.Code);
        Assert.Equal(SapApprovalDecisions.Approved, decision.Decision);
        Assert.Equal("Approved in ShopInventory by washbay: Crates counted", decision.Remarks);
        Assert.False(decision.Token.CanBeCanceled);
        Assert.True(result.Value.CanAdd);
        Assert.Equal(89165, result.Value.DraftEntry);
        Assert.Contains("ready to add", result.Value.Message);
        Assert.Contains(audit.Entries, entry => entry.Action == AuditActions.ApproveSapTransfer && entry.Success);
    }

    [Fact]
    public async Task An_approval_SAP_still_holds_for_another_template_is_not_ready_to_add()
    {
        var washbay = await UserAsync(ApplicationRoles.WashBay);
        var sap = WaitingTransfer(WashbayStage, draftAfterApproval: SapStockTransferAuthorizationStatuses.Pending);

        var result = await Handler(sap).Handle(
            new DecideSapTransferApprovalCommand(98635, ApprovalDecisionValues.Approved, null, washbay.Id, "washbay", null),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.False(result.Value.CanAdd);
        Assert.True(result.Value.StillPending);
        Assert.Contains("another approval", result.Value.Message);
    }

    [Fact]
    public async Task The_wash_bay_rejects_a_transfer_waiting_at_its_stage()
    {
        var washbay = await UserAsync(ApplicationRoles.WashBay);
        var sap = WaitingTransfer(WashbayStage);

        var result = await Handler(sap).Handle(
            new DecideSapTransferApprovalCommand(98635, ApprovalDecisionValues.NotApproved, "Wrong crates", washbay.Id, "washbay", null),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(SapApprovalDecisions.NotApproved, Assert.Single(sap.Decisions).Decision);
        Assert.False(result.Value.CanAdd);
        Assert.StartsWith("Rejected.", result.Value.Message);
    }

    [Fact]
    public async Task A_transfer_waiting_at_another_stage_is_refused_and_nothing_reaches_SAP()
    {
        var washbay = await UserAsync(ApplicationRoles.WashBay);
        var sap = WaitingTransfer(OtherStage);

        var result = await Handler(sap).Handle(
            new DecideSapTransferApprovalCommand(98635, ApprovalDecisionValues.Approved, null, washbay.Id, "washbay", null),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("CreditNoteApproval.OutsideStageScope", result.FirstError.Code);
        Assert.Empty(sap.Decisions);
    }

    [Fact]
    public async Task A_credit_memo_request_is_not_decided_through_the_transfer_route()
    {
        var washbay = await UserAsync(ApplicationRoles.WashBay);
        var sap = WaitingTransfer(WashbayStage);
        sap.Requests[0].ObjectType = SapObjectTypes.CreditNote;

        var result = await Handler(sap).Handle(
            new DecideSapTransferApprovalCommand(98635, ApprovalDecisionValues.Approved, null, washbay.Id, "washbay", null),
            CancellationToken.None);

        Assert.Equal("SapTransferApproval.RequestNotFound", result.FirstError.Code);
        Assert.Empty(sap.Decisions);
    }

    [Fact]
    public async Task A_stage_SAP_does_not_list_the_apps_approver_on_is_refused_with_why()
    {
        var washbay = await UserAsync(ApplicationRoles.WashBay);
        var sap = WaitingTransfer(WashbayStage);

        var result = await Handler(sap, lookups: Lookups(approvers: 51)).Handle(
            new DecideSapTransferApprovalCommand(98635, ApprovalDecisionValues.Approved, null, washbay.Id, "washbay", null),
            CancellationToken.None);

        Assert.Equal("CreditNoteApproval.ApproverNotOnStage", result.FirstError.Code);
        Assert.Empty(sap.Decisions);
    }

    [Fact]
    public async Task The_same_click_sent_twice_reaches_SAP_once()
    {
        var washbay = await UserAsync(ApplicationRoles.WashBay);
        var sap = WaitingTransfer(WashbayStage);
        var handler = Handler(sap);
        var command = new DecideSapTransferApprovalCommand(98635, ApprovalDecisionValues.Approved, null, washbay.Id, "washbay", "click-1");

        var first = await handler.Handle(command, CancellationToken.None);
        var second = await handler.Handle(command, CancellationToken.None);

        Assert.False(first.IsError);
        Assert.False(second.IsError);
        Assert.Single(sap.Decisions);
        Assert.Equal(first.Value.Message, second.Value.Message);
    }

    [Fact]
    public async Task The_drawer_offers_the_wash_bay_the_decision_at_its_stage()
    {
        var washbay = await UserAsync(ApplicationRoles.WashBay);
        var sap = WaitingTransfer(WashbayStage);

        var result = await Detail(sap, FixedStageScope.Stages("Factory Washbay", WashbayStage))
            .Handle(new GetSapTransferApprovalQuery(89165, washbay.Id), CancellationToken.None);

        var request = Assert.Single(result.Value.Approvals);
        Assert.True(request.CanDecide);
        Assert.Null(request.DecideNote);
        Assert.False(result.Value.Draft.CanAdd);
    }

    [Fact]
    public async Task The_drawer_says_why_a_transfer_at_another_stage_is_not_the_wash_bays()
    {
        var washbay = await UserAsync(ApplicationRoles.WashBay);
        var sap = WaitingTransfer(OtherStage);

        var result = await Detail(sap, FixedStageScope.Stages("Factory Washbay", WashbayStage))
            .Handle(new GetSapTransferApprovalQuery(89165, washbay.Id), CancellationToken.None);

        var request = Assert.Single(result.Value.Approvals);
        Assert.False(request.CanDecide);
        Assert.Contains("not one of yours to decide", request.DecideNote);
    }

    [Fact]
    public async Task A_stock_controller_sees_the_waiting_transfer_but_no_decision()
    {
        var controller = await UserAsync(ApplicationRoles.StockController);
        var sap = WaitingTransfer(WashbayStage);

        var result = await Detail(sap, FixedStageScope.EveryStage)
            .Handle(new GetSapTransferApprovalQuery(89165, controller.Id), CancellationToken.None);

        var request = Assert.Single(result.Value.Approvals);
        Assert.False(request.CanDecide);
        Assert.Null(request.DecideNote);
    }

    /// <summary>Draft entry 89165, PROYG 2 → WASHBAY, pending; request 98635 waiting at <paramref name="stage"/>.</summary>
    private static FakeTransferSap WaitingTransfer(
        int stage,
        string draftAfterApproval = SapStockTransferAuthorizationStatuses.Approved,
        string from = "PROYG 2",
        string to = "WASHBAY")
    {
        var draft = HeldTransfers.Draft(89165, from, to);
        draft.DocNum = 96285;
        draft.AuthorizationStatus = SapStockTransferAuthorizationStatuses.Pending;

        var request = HeldTransfers.Request(98635, 89165, SapApprovalRequestStatuses.Pending);
        request.CurrentStage = stage;
        request.ApprovalTemplatesID = 91;
        request.ApprovalRequestLines =
        [
            new SAPApprovalRequestLine { StageCode = stage, UserID = 51, Status = SapApprovalDecisions.Pending },
            new SAPApprovalRequestLine { StageCode = stage, UserID = ServiceApproverKey, Status = SapApprovalDecisions.Pending }
        ];

        return new FakeTransferSap(draft, request) { DeciderUserId = ServiceApproverKey, DraftStatusAfterApproval = draftAfterApproval };
    }

    /// <summary>Stage 44 'Factory Washbay' and stage 39, each listing <paramref name="approvers"/>; the app decides as Software.</summary>
    private static FakeSapApprovalLookups Lookups(params int[] approvers)
    {
        var listed = approvers.Length > 0 ? approvers : [51, ServiceApproverKey];
        var lookups = new FakeSapApprovalLookups { ServiceApproverUserCode = Software.UserCode! };
        lookups.Users[Software.InternalKey] = Software;
        foreach (var (code, name) in new[] { (WashbayStage, "Factory Washbay"), (OtherStage, "Yoghurt") })
        {
            lookups.Stages[code] = new SAPApprovalStage
            {
                Code = code,
                Name = name,
                NoOfApproversRequired = 1,
                ApprovalStageApprovers = listed.Select(id => new SAPApprovalStageApprover { UserID = id }).ToList()
            };
        }

        return lookups;
    }

    private DecideSapTransferApprovalHandler Handler(
        FakeTransferSap sap,
        RecordingAuditService? audit = null,
        FakeSapApprovalLookups? lookups = null,
        ICreditNoteApprovalStageScope? scope = null) => new(
        sap.AsClient(),
        lookups ?? Lookups(),
        new SapTransferApprovalAccess(_context, new TransferWarehouseAuthorizer(_context)),
        scope ?? FixedStageScope.Stages("Factory Washbay", WashbayStage),
        new IdempotencyRequestStore(new SingleDbContextScopeFactory(_options), Options.Create(new SecuritySettings())),
        audit ?? new RecordingAuditService(),
        Options.Create(new SAPSettings { Enabled = true, Username = "Software", Password = "pw" }),
        NullLogger<DecideSapTransferApprovalHandler>.Instance);

    private GetSapTransferApprovalHandler Detail(FakeTransferSap sap, ICreditNoteApprovalStageScope scope) => new(
        sap.AsClient(),
        Lookups(),
        new SapTransferApprovalAccess(_context, new TransferWarehouseAuthorizer(_context)),
        scope,
        new UserManagementService(_context, new MemoryCache(new MemoryCacheOptions()), NullLogger<UserManagementService>.Instance),
        Options.Create(new SAPSettings { Enabled = true }),
        NullLogger<GetSapTransferApprovalHandler>.Instance);

    private async Task<User> UserAsync(string role, string warehouse = "WASHBAY")
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
        user.SetWarehouseCodes([warehouse]);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }
}
