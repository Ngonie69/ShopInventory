using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers;
using ShopInventory.Features.InventoryTransfers.Commands.CreateInventoryTransfer;
using ShopInventory.Features.InventoryTransfers.Commands.RetryPendingTransferPost;
using ShopInventory.Features.InventoryTransfers.Queries.PreviewTransferApproval;
using ShopInventory.Features.InventoryTransfers.SapApproval;
using ShopInventory.Features.SapTransferApprovals.Commands.AddApprovedSapTransfer;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Depot and factory transfers (decision 2026-11-16). A transfer touching a depot or a
/// van keeps the app's own approval; any other is a factory transfer, which the app's approval does not
/// hold: it goes to SAP as a draft where one of SAP's templates covers its route — for a B1 user to add,
/// which starts SAP's approval — is added here once SAP approves it, and posts where no template covers it.
/// </summary>
public sealed class FactoryTransferTypeTests : IDisposable
{
    private const string CmfsTemplateName = "Transfer To CMFS1";
    private const string ToCmfsQuery =
        "SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0.\"ToWhsCode\"= $[OWTR.ToWhsCode] and T0.\"ToWhsCode\" = 'CMFS'";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _templateCache = new MemoryCache(new MemoryCacheOptions());

    public FactoryTransferTypeTests()
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

    // ── Which type ─────────────────────────────

    [Theory]
    [InlineData("WAREH", "PROYG 2", TransferTypes.Factory)]
    [InlineData("CMB", "CMFS", TransferTypes.Factory)]
    [InlineData("DISPCH", "DEPOTRAN", TransferTypes.Factory)]
    [InlineData("DEPOTRAN", "KEFGRC", TransferTypes.Depot)]
    [InlineData("KEFBYS", "WAREH", TransferTypes.Depot)]
    [InlineData("DISPYOG", "KEFSHOP", TransferTypes.Factory)]
    [InlineData("kefshop", "DISPCH", TransferTypes.Factory)]
    [InlineData("KEFGRC", "VAN010", TransferTypes.Depot)]
    public void The_warehouses_decide_the_type(string from, string to, string expected)
        => Assert.Equal(expected, TransferTypes.Of(from, to));

    [Fact]
    public void One_line_touching_a_depot_makes_the_whole_transfer_a_depot_transfer()
        => Assert.Equal(TransferTypes.Depot, TransferTypes.Of("DISPCH", "DISPYOG", ["DISPCH", "CORMACH2"]));

    // ── The app's approval ─────────────────────────────

    [Fact]
    public async Task A_factory_request_opens_settled_with_no_stage_and_alerts_nobody()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var notifications = new NoOpNotificationService();

        var request = await ApprovalService(notifications).EnsureRequestAsync(Request("WAREH", "PROYG 2"), officer.Id, default);

        Assert.Equal(ApprovalRequestStatuses.Approved, request.Status);
        Assert.Equal(TransferTypes.SapApprovalTemplateName, request.TemplateName);
        Assert.True(InventoryTransferApprovalService.IsSapApproval(request));
        Assert.Equal(officer.Id, request.OriginatorUserId);
        var (_, stages) = await ApprovalService().GetProgressAsync(
            ApprovalDocumentContext.ForTransferRequest(Request("WAREH", "PROYG 2")), default);
        Assert.Empty(stages);
        Assert.Empty(notifications.Sent);
    }

    [Fact]
    public async Task Stock_into_the_factory_shop_follows_SAPs_approval_not_the_apps()
    {
        // The user's call (2026-11-16): KEFSHOP is not a depot. The app's "Dispatch to Factory Shop"
        // template no longer holds it.
        var officer = await AddUserAsync(ApplicationRoles.StockController);

        var request = await ApprovalService().EnsureRequestAsync(Request("DISPYOG", "KEFSHOP"), officer.Id, default);

        Assert.True(InventoryTransferApprovalService.IsSapApproval(request));
        Assert.Equal(ApprovalRequestStatuses.Approved, request.Status);
    }

    [Fact]
    public async Task A_depot_request_still_waits_on_the_apps_stages()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);

        var request = await ApprovalService().EnsureRequestAsync(Request("WAREH", "KEFGRC"), officer.Id, default);

        Assert.Equal(ApprovalRequestStatuses.Pending, request.Status);
        Assert.False(InventoryTransferApprovalService.IsSapApproval(request));
    }

    [Fact]
    public async Task Changing_a_factory_transfer_keeps_it_with_SAP()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var pending = await AddPendingAsync(officer, "CMB", "CMFS");
        var document = ApprovalDocumentContext.ForPendingTransfer(pending);
        await ApprovalService().EnsureRequestAsync(document, officer.Id, default);

        var reopened = await ApprovalService().ReopenAsync(document, officer.Id, default);

        Assert.Equal(ApprovalRequestStatuses.Approved, reopened.Status);
        Assert.True(InventoryTransferApprovalService.IsSapApproval(reopened));
    }

    [Fact]
    public async Task A_change_that_puts_a_depot_on_it_sends_it_to_the_apps_approval()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var pending = await AddPendingAsync(officer, "CMB", "CMFS");
        await ApprovalService().EnsureRequestAsync(ApprovalDocumentContext.ForPendingTransfer(pending), officer.Id, default);

        pending.ToWarehouse = "KEFGRC";
        pending.PayloadJson = PendingInventoryTransferMapper.SerializePayload(Payload("CMB", "KEFGRC"));
        await _context.SaveChangesAsync();
        var reopened = await ApprovalService().ReopenAsync(ApprovalDocumentContext.ForPendingTransfer(pending), officer.Id, default);

        Assert.Equal(ApprovalRequestStatuses.Pending, reopened.Status);
        Assert.False(InventoryTransferApprovalService.IsSapApproval(reopened));
    }

    [Fact]
    public async Task The_preview_names_the_SAP_template_that_will_hold_a_factory_transfer()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new FakeSap { Covers = true };

        var preview = await new PreviewTransferApprovalHandler(ApprovalService(), Routes(sap))
            .Handle(new PreviewTransferApprovalQuery(["CMB"], ["CMFS"], officer.Id), default);

        Assert.False(preview.IsError);
        Assert.Equal(TransferTypes.Factory, preview.Value.TransferType);
        Assert.True(preview.Value.SapApprovalKnown);
        Assert.Equal([CmfsTemplateName], preview.Value.SapApprovalTemplates);
        Assert.Empty(preview.Value.Stages);
    }

    [Fact]
    public async Task The_preview_of_a_depot_transfer_never_reads_SAP()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);

        var preview = await new PreviewTransferApprovalHandler(ApprovalService(), Routes(new FakeSap { TemplatesUnreadable = true }))
            .Handle(new PreviewTransferApprovalQuery(["WAREH"], ["KEFGRC"], officer.Id), default);

        Assert.Equal(TransferTypes.Depot, preview.Value.TransferType);
        Assert.NotEmpty(preview.Value.Stages);
    }

    // ── Submitting ─────────────────────────────

    [Fact]
    public async Task A_factory_transfer_goes_to_SAP_as_it_is_submitted_and_asks_no_approver()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var steps = new RecordingSteps(_context);

        var result = await CreateHandler(steps).Handle(
            new CreateInventoryTransferCommand(Payload("CMB", "CMFS"), officer.Id), default);

        Assert.False(result.IsError, string.Join("; ", result.ErrorsOrEmptyList.Select(error => error.Description)));
        var pending = Assert.Single(await _context.PendingInventoryTransfers.AsNoTracking().ToListAsync());
        Assert.Equal(TransferTypes.Factory, pending.TransferType);
        Assert.Equal([pending.Id], steps.Advanced);
        Assert.Equal(PendingInventoryTransferStatuses.HeldInSap, result.Value.PendingTransfer!.Status);
        Assert.Equal("Saved to SAP as draft 501.", result.Value.Message);
        Assert.False(result.Value.RequiresApproval);
        Assert.Equal(ApprovalRequestStatuses.Approved, Assert.Single(_context.ApprovalRequests).Status);
    }

    [Fact]
    public async Task A_depot_transfer_is_still_held_for_the_apps_approval()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var steps = new RecordingSteps(_context);

        var result = await CreateHandler(steps).Handle(
            new CreateInventoryTransferCommand(Payload("WAREH", "KEFGRC"), officer.Id), default);

        Assert.False(result.IsError, string.Join("; ", result.ErrorsOrEmptyList.Select(error => error.Description)));
        Assert.Empty(steps.Advanced);
        Assert.True(result.Value.RequiresApproval);
        Assert.Equal(TransferTypes.Depot, (await _context.PendingInventoryTransfers.AsNoTracking().SingleAsync()).TransferType);
    }

    // ── Through SAP ─────────────────────────────

    [Fact]
    public async Task On_a_route_SAP_approves_it_is_saved_as_a_draft_carrying_its_draft_number()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS");
        var sap = new FakeSap { Covers = true };
        var poster = new RecordingPoster(_context);

        var step = await Steps(sap, poster).AdvanceAsync(pending, user.Id, user.Username);

        Assert.False(step.IsError, string.Join("; ", step.ErrorsOrEmptyList.Select(error => error.Description)));
        Assert.Null(step.Value.Transfer);
        Assert.Contains("draft 501", step.Value.Message);
        var draft = Assert.Single(sap.SavedDrafts);
        Assert.Equal(pending.DraftNumber, draft.OrderNumber);
        Assert.StartsWith("Reason: restock | Requested by", draft.Comments);
        Assert.Equal(PendingInventoryTransferStatuses.HeldInSap, pending.Status);
        Assert.Equal(FactoryTransferSapStates.AwaitingSapClient, pending.SapApprovalState);
        Assert.Equal((90001, 501), (pending.SapDraftDocEntry, pending.SapDraftDocNum));
        Assert.Equal(CmfsTemplateName, pending.SapApprovalTemplates);
        Assert.Empty(poster.Posted);
    }

    [Fact]
    public async Task On_a_route_no_SAP_template_covers_it_posts_at_once()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMH");
        var sap = new FakeSap { Covers = true };
        var poster = new RecordingPoster(_context);

        var step = await Steps(sap, poster).AdvanceAsync(pending, user.Id, user.Username);

        Assert.False(step.IsError);
        Assert.Equal(4321, step.Value.Transfer?.DocNum);
        Assert.Equal([pending.Id], poster.Posted);
        Assert.Empty(sap.SavedDrafts);
    }

    [Theory]
    [InlineData("sasWithout", FactoryTransferSapStates.AwaitingSapClient, "waiting to be added in the SAP client")]
    [InlineData("sasPending", FactoryTransferSapStates.Pending, "waiting for SAP's approval")]
    public async Task Checking_again_reports_the_draft_without_saving_another(string status, string state, string says)
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS");
        var sap = new FakeSap { Covers = true, Drafts = [Draft(pending, status)] };

        var step = await Steps(sap, new RecordingPoster(_context)).AdvanceAsync(pending, user.Id, user.Username);

        Assert.False(step.IsError);
        Assert.Contains(says, step.Value.Message);
        Assert.Equal(PendingInventoryTransferStatuses.HeldInSap, pending.Status);
        Assert.Equal(state, pending.SapApprovalState);
        Assert.Empty(sap.SavedDrafts);
        Assert.Empty(sap.Adds);
    }

    [Fact]
    public async Task A_draft_SAP_rejected_rejects_the_transfer()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS");
        var sap = new FakeSap { Covers = true, Drafts = [Draft(pending, "sasRejected")] };

        var step = await Steps(sap, new RecordingPoster(_context)).AdvanceAsync(pending, user.Id, user.Username);

        Assert.False(step.IsError);
        Assert.Equal(PendingInventoryTransferStatuses.Rejected, pending.Status);
        Assert.Equal(FactoryTransferSapStates.Rejected, pending.SapApprovalState);
        Assert.Empty(sap.Adds);
    }

    [Fact]
    public async Task Once_SAP_approves_the_draft_it_is_added_and_the_transfer_recorded()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS");
        var sap = new FakeSap { Covers = true, Drafts = [Draft(pending, "sasApproved")] };
        var poster = new RecordingPoster(_context);

        var step = await Steps(sap, poster).AdvanceAsync(pending, user.Id, user.Username);

        Assert.False(step.IsError, string.Join("; ", step.ErrorsOrEmptyList.Select(error => error.Description)));
        var add = Assert.Single(sap.Adds);
        Assert.Equal(90001, add.DraftEntry);
        Assert.True(add.CallerAuthorized);
        Assert.Equal(8801, step.Value.Transfer?.DocNum);
        Assert.Equal(PendingInventoryTransferStatuses.Posted, pending.Status);
        Assert.Equal(8801, pending.SapDocNum);
    }

    // ── Remarks: who requested it and who approved it in SAP ─────────────────────────────

    [Fact]
    public async Task The_draft_names_the_requester_and_when()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS");
        pending.CreatedAtUtc = new DateTime(2026, 11, 18, 6, 30, 0, DateTimeKind.Utc);
        var sap = new FakeSap { Covers = true };

        await Steps(sap, new RecordingPoster(_context)).AdvanceAsync(pending, user.Id, user.Username);

        var saved = Assert.Single(sap.SavedDrafts);
        Assert.Equal($"Reason: restock | Requested by {user.Username} on 18 Nov 2026 08:30", saved.Comments);
    }

    [Fact]
    public async Task Once_SAP_approves_the_transfer_remarks_name_the_requester_and_SAPs_approver_with_times()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS");
        pending.CreatedAtUtc = new DateTime(2026, 11, 18, 6, 30, 0, DateTimeKind.Utc);
        var sap = new FakeSap
        {
            Covers = true,
            Drafts = [Draft(pending, "sasApproved")],
            Approvals = [Approval(90001, (12, "ardApproved", "2026-11-18T00:00:00Z", "09:41:00"), (13, "ardPending", null, null))]
        };

        var step = await Steps(sap, new RecordingPoster(_context)).AdvanceAsync(pending, user.Id, user.Username);

        Assert.False(step.IsError);
        var (docEntry, comments) = Assert.Single(sap.CommentUpdates);
        Assert.Equal(8800, docEntry);
        Assert.Equal(
            $"Reason: restock | Requested by {user.Username} on 18 Nov 2026 08:30 | Approved by Whouse Approver on 18 Nov 2026 09:41",
            comments);
    }

    [Fact]
    public async Task A_draft_added_in_the_SAP_client_gets_SAPs_approver_written_in_too()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS");
        pending.CreatedAtUtc = new DateTime(2026, 11, 18, 6, 30, 0, DateTimeKind.Utc);
        pending.SapDraftDocEntry = 90001;
        var drafted = $"Reason: restock | Requested by {user.Username} on 18 Nov 2026 08:30";
        var sap = new FakeSap
        {
            Covers = true,
            InSap = new InventoryTransfer { DocEntry = 8800, DocNum = 8801, FromWarehouse = "CMB", ToWarehouse = "CMFS", Comments = drafted },
            Approvals = [Approval(90001, (12, "ardApproved", "2026-11-18", "10:05:00"), (14, "ardApproved", "2026-11-18", "09:50:00"))]
        };

        var step = await Steps(sap, new RecordingPoster(_context)).AdvanceAsync(pending, user.Id, user.Username);

        Assert.False(step.IsError);
        var (_, comments) = Assert.Single(sap.CommentUpdates);
        Assert.Equal(
            $"{drafted} | Approved by Opsassist on 18 Nov 2026 09:50, Whouse Approver on 18 Nov 2026 10:05",
            comments);
    }

    [Fact]
    public async Task Remarks_a_B1_user_changed_are_left_as_they_are()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS");
        pending.SapDraftDocEntry = 90001;
        var sap = new FakeSap
        {
            Covers = true,
            InSap = new InventoryTransfer { DocEntry = 8800, DocNum = 8801, FromWarehouse = "CMB", ToWarehouse = "CMFS", Comments = "Keyed by stores" },
            Approvals = [Approval(90001, (12, "ardApproved", "2026-11-18", "10:05:00"))]
        };

        var step = await Steps(sap, new RecordingPoster(_context)).AdvanceAsync(pending, user.Id, user.Username);

        Assert.False(step.IsError);
        Assert.Empty(sap.CommentUpdates);
    }

    [Fact]
    public async Task The_transfer_is_still_recorded_when_SAPs_approvers_cannot_be_read()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS");
        var sap = new FakeSap { Covers = true, Drafts = [Draft(pending, "sasApproved")], ApprovalsUnreadable = true };

        var step = await Steps(sap, new RecordingPoster(_context)).AdvanceAsync(pending, user.Id, user.Username);

        Assert.False(step.IsError);
        Assert.Equal(PendingInventoryTransferStatuses.Posted, pending.Status);
        Assert.Empty(sap.CommentUpdates);
    }

    [Fact]
    public async Task A_long_reason_gives_way_to_the_requester_and_approver()
    {
        var pending = new PendingInventoryTransferEntity
        {
            CreatedByName = "stores.clerk",
            CreatedAtUtc = new DateTime(2026, 11, 18, 6, 30, 0, DateTimeKind.Utc)
        };

        var remarks = InventoryTransferRemarks.BuildForSapApproval(
            pending, [("Whouse Approver", new DateTime(2026, 11, 18, 9, 41, 0))], new string('x', 400));

        Assert.Equal(InventoryTransferRemarks.MaxLength, remarks.Length);
        Assert.EndsWith(" | Requested by stores.clerk on 18 Nov 2026 08:30 | Approved by Whouse Approver on 18 Nov 2026 09:41", remarks);
    }

    [Theory]
    [InlineData("2026-11-18T00:00:00Z", "09:41:00", "2026-11-18 09:41")]
    [InlineData("2026-11-18", "14:05", "2026-11-18 14:05")]
    [InlineData("2026-11-18", null, null)]
    [InlineData(null, "09:41:00", null)]
    public void SAPs_decision_time_is_read_on_SAPs_own_clock(string? date, string? time, string? expected)
        => Assert.Equal(expected, FactoryTransferSapApproval.SapLocalTime(date, time)?.ToString("yyyy-MM-dd HH:mm"));

    [Fact]
    public async Task A_draft_added_in_the_SAP_client_is_found_and_recorded_without_another_draft()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS");
        var sap = new FakeSap
        {
            Covers = true,
            InSap = new InventoryTransfer { DocEntry = 8800, DocNum = 8801, FromWarehouse = "CMB", ToWarehouse = "CMFS" }
        };

        var step = await Steps(sap, new RecordingPoster(_context)).AdvanceAsync(pending, user.Id, user.Username);

        Assert.False(step.IsError);
        Assert.Equal(8801, step.Value.Transfer?.DocNum);
        Assert.Equal(PendingInventoryTransferStatuses.Posted, pending.Status);
        Assert.Empty(sap.SavedDrafts);
        Assert.Empty(sap.Adds);
    }

    [Fact]
    public async Task Nothing_is_sent_when_SAP_templates_cannot_be_read()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS");
        var sap = new FakeSap { TemplatesUnreadable = true };
        var poster = new RecordingPoster(_context);

        var step = await Steps(sap, poster).AdvanceAsync(pending, user.Id, user.Username);

        Assert.True(step.IsError);
        Assert.Equal("InventoryTransfer.SapConnectionError", step.FirstError.Code);
        Assert.Empty(sap.SavedDrafts);
        Assert.Empty(poster.Posted);
        Assert.Equal(PendingInventoryTransferStatuses.Approved, pending.Status);
    }

    [Fact]
    public async Task Check_SAP_on_a_held_factory_transfer_answers_where_it_stands()
    {
        var (pending, user) = await ClearedFactoryTransferAsync("CMB", "CMFS", ApplicationRoles.Admin);
        pending.Status = PendingInventoryTransferStatuses.HeldInSap;
        await _context.SaveChangesAsync();
        var sap = new FakeSap { Covers = true, Drafts = [Draft(pending, "sasPending")] };
        var handler = new RetryPendingTransferPostHandler(
            _context, new TransferWarehouseAuthorizer(_context), new RecordingPoster(_context),
            Steps(sap, new RecordingPoster(_context)), Options.Create(new SAPSettings { Enabled = true }));

        var result = await handler.Handle(new RetryPendingTransferPostCommand(pending.Id, user.Id), default);

        Assert.False(result.IsError, string.Join("; ", result.ErrorsOrEmptyList.Select(error => error.Description)));
        Assert.Equal(PendingInventoryTransferStatuses.HeldInSap, result.Value.Status);
        Assert.Contains("waiting for SAP's approval", result.Value.Message);
        Assert.Null(result.Value.Transfer);
    }

    // ── Builders ─────────────────────────────

    private InventoryTransferApprovalService ApprovalService(NoOpNotificationService? notifications = null) =>
        new(_context, notifications ?? new NoOpNotificationService(), NullLogger<InventoryTransferApprovalService>.Instance);

    private SapTransferApprovalRoutes Routes(FakeSap sap) =>
        new(sap.AsClient(), _templateCache, NullLogger<SapTransferApprovalRoutes>.Instance);

    private FactoryTransferSapApproval Steps(FakeSap sap, IPendingInventoryTransferPoster poster) => new(
        _context, sap.AsClient(), Routes(sap), poster, new AcquiringStore(), new NoOpAuditService(), sap.AsSender(),
        NullLogger<FactoryTransferSapApproval>.Instance);

    private CreateInventoryTransferHandler CreateHandler(IFactoryTransferSapApproval steps) => new(
        _context,
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name == nameof(ISAPServiceLayerClient.GetWarehousesAsync)
            ? Task.FromResult(new[] { "CMB", "CMFS", "WAREH", "KEFGRC" }.Select(code => new WarehouseDto { WarehouseCode = code }).ToList())
            : null),
        StubProxy.For<IStockValidationService>((method, _) => method.Name == nameof(IStockValidationService.ValidateInventoryTransferStockAsync)
            ? Task.FromResult(new StockValidationResult())
            : null),
        ApprovalService(),
        StubProxy.Unused<IPendingInventoryTransferPoster>(),
        steps,
        new NoOpAuditService(),
        StubProxy.Unused<IIdempotencyRequestStore>(),
        Options.Create(new SAPSettings { Enabled = true, TransferStockValidationBudgetSeconds = 5 }),
        NullLogger<CreateInventoryTransferHandler>.Instance);

    private static InventoryTransferRequest Request(string from, string to) => new()
    {
        DocEntry = 7001,
        DocNum = 7001,
        DocumentStatus = "bost_Open",
        FromWarehouse = from,
        ToWarehouse = to,
        StockTransferLines = [new InventoryTransferRequestLine { LineNum = 0, ItemCode = "ICA004", Quantity = 5, FromWarehouseCode = from, WarehouseCode = to }]
    };

    private static CreateInventoryTransferRequest Payload(string from, string to) => new()
    {
        FromWarehouse = from,
        ToWarehouse = to,
        DocDate = "2026-11-16",
        Comments = "Reason: restock",
        Lines = [new CreateInventoryTransferLineRequest { ItemCode = "ICA004", Quantity = 5, UoMCode = "EA" }]
    };

    private async Task<PendingInventoryTransferEntity> AddPendingAsync(User submitter, string from, string to)
    {
        var pending = new PendingInventoryTransferEntity
        {
            DraftNumber = $"DT-2026-{Random.Shared.Next(10000, 99999)}",
            FromWarehouse = from,
            ToWarehouse = to,
            PayloadJson = PendingInventoryTransferMapper.SerializePayload(Payload(from, to)),
            CreatedByUserId = submitter.Id,
            CreatedByName = submitter.Username,
            CreatedByRole = submitter.Role,
            LineCount = 1,
            TotalQuantity = 5m,
            TransferType = TransferTypes.Of(from, to)
        };
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();
        return pending;
    }

    /// <summary>A factory transfer as submission leaves it just before its first step: cleared by the app.</summary>
    private async Task<(PendingInventoryTransferEntity Pending, User User)> ClearedFactoryTransferAsync(
        string from, string to, string role = ApplicationRoles.StockController)
    {
        var user = await AddUserAsync(role);
        var pending = await AddPendingAsync(user, from, to);
        pending.Status = PendingInventoryTransferStatuses.Approved;
        await _context.SaveChangesAsync();
        return (pending, user);
    }

    private static SAPStockTransferDraft Draft(PendingInventoryTransferEntity pending, string status) => new()
    {
        DocEntry = 90001,
        DocNum = 501,
        DocObjectCode = "67",
        DocumentStatus = "bost_Open",
        AuthorizationStatus = status,
        FromWarehouse = pending.FromWarehouse,
        ToWarehouse = pending.ToWarehouse,
        U_OrderNumber = pending.DraftNumber
    };

    /// <summary>SAP's approval request on a draft, one line per approver: (user, decision, date, time).</summary>
    private static SAPApprovalRequest Approval(
        int draftEntry, params (int UserId, string Status, string? Date, string? Time)[] lines) => new()
    {
        Code = 74432,
        DraftEntry = draftEntry,
        Status = "arsApproved",
        ApprovalRequestLines = lines.Select(line => new SAPApprovalRequestLine
        {
            StageCode = 16,
            UserID = line.UserId,
            Status = line.Status,
            UpdateDate = line.Date,
            UpdateTime = line.Time
        }).ToList()
    };

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

    /// <summary>The SAP reads and writes a factory transfer's step makes.</summary>
    private sealed class FakeSap
    {
        public bool Covers { get; init; }
        public bool TemplatesUnreadable { get; init; }
        public List<SAPStockTransferDraft> Drafts { get; init; } = [];
        public InventoryTransfer? InSap { get; init; }
        public List<CreateInventoryTransferRequest> SavedDrafts { get; } = [];
        public List<AddApprovedSapTransferCommand> Adds { get; } = [];
        public List<SAPApprovalRequest> Approvals { get; init; } = [];
        public bool ApprovalsUnreadable { get; init; }
        public List<(int DocEntry, string Comments)> CommentUpdates { get; } = [];

        private static readonly List<SAPUser> Users =
        [
            new() { InternalKey = 12, UserCode = "Whouse", UserName = "Whouse Approver" },
            new() { InternalKey = 14, UserCode = "Opsassist", UserName = " " }
        ];

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetStockTransferApprovalRequestsAsync) => ApprovalsUnreadable
                    ? Task.FromException<List<SAPApprovalRequest>>(new HttpRequestException("SAP is down"))
                    : Task.FromResult(Approvals
                        .Where(request => ((IReadOnlyCollection<int>)args![0]!).Contains(request.DraftEntry ?? 0))
                        .ToList()),
                nameof(ISAPServiceLayerClient.GetSapUsersAsync) => Task.FromResult(Users
                    .Where(user => ((IEnumerable<int>)args![0]!).Contains(user.InternalKey))
                    .ToList()),
                nameof(ISAPServiceLayerClient.UpdateStockTransferCommentsAsync) => UpdateComments((int)args![0]!, (string)args[1]!),
                nameof(ISAPServiceLayerClient.FindStockTransferByOrderNumberAsync) => Task.FromResult(InSap),
                nameof(ISAPServiceLayerClient.FindStockTransferDraftsByOrderNumberAsync) =>
                    Task.FromResult(Drafts.Where(draft => draft.U_OrderNumber == (string)args![0]!).ToList()),
                nameof(ISAPServiceLayerClient.CreateStockTransferDraftAsync) => SaveDraft((CreateInventoryTransferRequest)args![0]!),
                nameof(ISAPServiceLayerClient.GetApprovalTemplateConditionsAsync) => TemplatesUnreadable
                    ? Task.FromException<List<SAPApprovalTemplateCondition>>(new HttpRequestException("SAP is down"))
                    : Task.FromResult(Covers ? [CmfsTemplate()] : new List<SAPApprovalTemplateCondition>()),
                nameof(ISAPServiceLayerClient.GetUserQueriesAsync) => Task.FromResult(
                    new List<SAPUserQuery> { new() { InternalKey = 337, QueryCategory = 19, Query = ToCmfsQuery } }),
                nameof(ISAPServiceLayerClient.GetInventoryTransferByDocEntryAsync) => Task.FromResult<InventoryTransfer?>(
                    new InventoryTransfer { DocEntry = (int)args![0]!, DocNum = 8801, FromWarehouse = "CMB", ToWarehouse = "CMFS" }),
                _ => null
            });

        public ISender AsSender() =>
            StubProxy.For<ISender>((method, args) => args?[0] switch
            {
                AddApprovedSapTransferCommand add => Add(add),
                _ => throw new InvalidOperationException($"Unexpected request: {args?[0]?.GetType().Name}")
            });

        private Task<ErrorOr<AddApprovedSapTransferResultDto>> Add(AddApprovedSapTransferCommand add)
        {
            Adds.Add(add);
            return Task.FromResult<ErrorOr<AddApprovedSapTransferResultDto>>(new AddApprovedSapTransferResultDto
            {
                DraftEntry = add.DraftEntry,
                TransferDocEntry = 8800,
                TransferDocNum = 8801,
                FromWarehouse = "CMB",
                ToWarehouse = "CMFS",
                Message = "Added as transfer #8801."
            });
        }

        private Task UpdateComments(int docEntry, string comments)
        {
            CommentUpdates.Add((docEntry, comments));
            return Task.CompletedTask;
        }

        private Task<SAPStockTransferDraft> SaveDraft(CreateInventoryTransferRequest transfer)
        {
            SavedDrafts.Add(transfer);
            return Task.FromResult(new SAPStockTransferDraft
            {
                DocEntry = 90001,
                DocNum = 501,
                DocObjectCode = "67",
                DocumentStatus = "bost_Open",
                AuthorizationStatus = "sasWithout",
                FromWarehouse = transfer.FromWarehouse,
                ToWarehouse = transfer.ToWarehouse,
                U_OrderNumber = transfer.OrderNumber
            });
        }

        private static SAPApprovalTemplateCondition CmfsTemplate() => new()
        {
            Code = 71,
            Name = CmfsTemplateName,
            IsActive = "tYES",
            UseTerms = "tYES",
            ApprovalTemplateDocuments = [new SAPApprovalTemplateDocument { DocumentType = "atdtInventoryTransfer" }],
            ApprovalTemplateQueries = [new SAPApprovalTemplateQuery { QueryID = 337 }]
        };
    }

    /// <summary>Settles the record as the real poster does, and counts what it was asked to do.</summary>
    private sealed class RecordingPoster(ApplicationDbContext context) : IPendingInventoryTransferPoster
    {
        public List<Guid> Posted { get; } = [];

        public async Task<ErrorOr<InventoryTransferDto>> PostAsync(
            PendingInventoryTransferEntity pending, Guid postedByUserId, bool postOnlyLinesInStock = false)
        {
            Posted.Add(pending.Id);
            pending.Status = PendingInventoryTransferStatuses.Posted;
            pending.SapDocEntry = 1234;
            pending.SapDocNum = 4321;
            await context.SaveChangesAsync();
            return new InventoryTransferDto { DocEntry = 1234, DocNum = 4321 };
        }

        public async Task<InventoryTransferDto> RecordAddedInSapAsync(
            PendingInventoryTransferEntity pending, InventoryTransfer transfer, Guid postedByUserId, string auditDetail)
        {
            pending.Status = PendingInventoryTransferStatuses.Posted;
            pending.SapDocEntry = transfer.DocEntry;
            pending.SapDocNum = transfer.DocNum;
            await context.SaveChangesAsync();
            return new InventoryTransferDto { DocEntry = transfer.DocEntry, DocNum = transfer.DocNum };
        }
    }

    /// <summary>Stands in for the SAP step at submission: records it and leaves the transfer held in SAP.</summary>
    private sealed class RecordingSteps(ApplicationDbContext context) : IFactoryTransferSapApproval
    {
        public List<Guid> Advanced { get; } = [];

        public async Task<ErrorOr<FactoryTransferStep>> AdvanceAsync(PendingInventoryTransferEntity pending, Guid userId, string username)
        {
            Advanced.Add(pending.Id);
            pending.Status = PendingInventoryTransferStatuses.HeldInSap;
            pending.SapDraftDocNum = 501;
            await context.SaveChangesAsync();
            return new FactoryTransferStep(null, "Saved to SAP as draft 501.");
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
