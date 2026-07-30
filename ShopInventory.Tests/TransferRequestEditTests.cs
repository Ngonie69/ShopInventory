using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers;
using ShopInventory.Features.InventoryTransfers.Commands.EditTransferRequest;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers changing an SAP transfer request: what a change is allowed to say, and the approval
/// path a change takes when its author does not run the warehouse the stock leaves.
/// </summary>
/// <remarks>
/// The warehouse split is the access rule here. A depot controller editing a request for their
/// own warehouse is adjusting their own paperwork; editing one for someone else's warehouse is
/// not, and must not reach SAP without a second person agreeing. Two things protect that: the
/// edit is held rather than written, and the approval template it routes to must have an
/// authorizer who is not the proposer.
/// </remarks>
public sealed class TransferRequestEditTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public TransferRequestEditTests()
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

    // ── What a change may say ───────────────────────────

    [Fact]
    public void A_quantity_may_be_adjusted()
    {
        var document = Document(("ITEM-1", 10m), ("ITEM-2", 4m));

        var (lines, error) = TransferRequestEditMapper.BuildProposedLines(document,
            [Keep(0, 6m), Keep(1, 4m)]);

        Assert.Null(error);
        Assert.Equal(2, lines!.Count);
        Assert.Equal(6m, lines[0].Quantity);
        // Detail the editor never sent is carried over from the document, not blanked.
        Assert.Equal("ITEM-1", lines[0].ItemCode);
    }

    [Fact]
    public void A_line_left_out_of_the_change_is_removed()
    {
        var document = Document(("ITEM-1", 10m), ("ITEM-2", 4m));

        var (lines, error) = TransferRequestEditMapper.BuildProposedLines(document, [Keep(0, 10m)]);

        Assert.Null(error);
        Assert.Single(lines!);
        Assert.Equal(0, lines![0].LineNum);
    }

    [Fact]
    public void A_line_that_is_not_on_the_request_is_refused()
    {
        var document = Document(("ITEM-1", 10m));

        var (lines, error) = TransferRequestEditMapper.BuildProposedLines(document, [Keep(7, 1m)]);

        Assert.Null(lines);
        Assert.Contains("not on transfer request", error);
    }

    [Fact]
    public void The_same_line_cannot_be_sent_twice()
    {
        // Two entries for one line would otherwise leave which quantity wins up to ordering.
        var document = Document(("ITEM-1", 10m));

        var (lines, error) = TransferRequestEditMapper.BuildProposedLines(document, [Keep(0, 3m), Keep(0, 5m)]);

        Assert.Null(lines);
        Assert.Contains("more than once", error);
    }

    [Fact]
    public void A_kept_line_cannot_be_zeroed_out()
    {
        // Zero would reach SAP as a line that exists but moves nothing; removal is the way.
        var document = Document(("ITEM-1", 10m));

        var (lines, error) = TransferRequestEditMapper.BuildProposedLines(document, [Keep(0, 0m)]);

        Assert.Null(lines);
        Assert.Contains("Remove the line instead", error);
    }

    [Fact]
    public void Every_line_cannot_be_removed()
    {
        var document = Document(("ITEM-1", 10m));

        var (lines, error) = TransferRequestEditMapper.BuildProposedLines(document, []);

        Assert.Null(lines);
        Assert.Contains("At least one line", error);
    }

    [Fact]
    public void A_change_that_changes_nothing_is_recognised()
    {
        var document = Document(("ITEM-1", 10m), ("ITEM-2", 4m));
        var original = TransferRequestEditMapper.FromDocument(document);
        var (proposed, _) = TransferRequestEditMapper.BuildProposedLines(document, [Keep(0, 10m), Keep(1, 4m)]);

        Assert.True(TransferRequestEditMapper.IsNoOp(original, proposed!));
    }

    [Fact]
    public void Dropping_a_line_is_not_a_no_op()
    {
        var document = Document(("ITEM-1", 10m), ("ITEM-2", 4m));
        var original = TransferRequestEditMapper.FromDocument(document);
        var (proposed, _) = TransferRequestEditMapper.BuildProposedLines(document, [Keep(0, 10m)]);

        Assert.False(TransferRequestEditMapper.IsNoOp(original, proposed!));
    }

    [Fact]
    public void A_held_change_survives_the_round_trip_through_storage()
    {
        var document = Document(("ITEM-1", 10m), ("ITEM-2", 4m));
        var (proposed, _) = TransferRequestEditMapper.BuildProposedLines(document, [Keep(0, 6m)]);

        var edit = HeldEdit(Guid.NewGuid());
        edit.OriginalLinesJson = TransferRequestEditMapper.Serialize(TransferRequestEditMapper.FromDocument(document));
        edit.ProposedLinesJson = TransferRequestEditMapper.Serialize(proposed!);

        var dto = TransferRequestEditMapper.ToDto(edit);

        Assert.Equal(2, dto.OriginalLines.Count);
        Assert.Single(dto.ProposedLines);
        Assert.Equal(6m, dto.ProposedLines[0].Quantity);
    }

    [Fact]
    public void An_unreadable_stored_change_yields_no_lines_rather_than_a_partial_one()
    {
        // The applier refuses on an empty list. Returning half a document would silently
        // delete whatever it failed to parse.
        var edit = HeldEdit(Guid.NewGuid());
        edit.ProposedLinesJson = "{ not json";

        Assert.Empty(TransferRequestEditMapper.ToDto(edit).ProposedLines);
    }

    // ── Warehouse scope ─────────────────────────────────

    [Fact]
    public async Task A_depot_controller_may_act_on_a_request_out_of_any_warehouse_they_run()
    {
        var user = await AddUserAsync(ApplicationRoles.DepotController, "KEFBYC", "VAN010");

        var result = await Authorizer().EnsureCanActOnSourceAsync(user.Id, "VAN010", default);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task A_depot_controller_may_not_act_on_a_request_out_of_another_warehouse()
    {
        var user = await AddUserAsync(ApplicationRoles.DepotController, "KEFBYC", "VAN010");

        var result = await Authorizer().EnsureCanActOnSourceAsync(user.Id, "CORMACH", default);

        Assert.True(result.IsError);
    }

    // ── Reassigning a warehouse ─────────────────────────

    [Fact]
    public void A_warehouse_the_request_already_has_is_not_a_change()
    {
        var document = Document(("ITEM-1", 10m));

        var (change, error) = TransferRequestEditMapper.BuildWarehouseChange(document, "cormach", "KEFBYC");

        Assert.Null(error);
        Assert.False(change.ChangesAnything);
    }

    [Fact]
    public void An_omitted_warehouse_leaves_the_requests_own()
    {
        var document = Document(("ITEM-1", 10m));

        var (change, error) = TransferRequestEditMapper.BuildWarehouseChange(document, null, "   ");

        Assert.Null(error);
        Assert.False(change.ChangesAnything);
    }

    [Fact]
    public void Either_end_of_the_transfer_may_be_moved()
    {
        var document = Document(("ITEM-1", 10m));

        var (change, error) = TransferRequestEditMapper.BuildWarehouseChange(document, " VAN010 ", "KEFBYD");

        Assert.Null(error);
        Assert.Equal("VAN010", change.FromWarehouse);
        Assert.Equal("KEFBYD", change.ToWarehouse);
        Assert.Equal(["VAN010", "KEFBYD"], change.NamedWarehouses);
    }

    [Fact]
    public void A_request_cannot_be_moved_onto_its_own_other_end()
    {
        // Only the source is sent, but it lands on the destination the document already has —
        // which SAP would take as a transfer from a warehouse to itself.
        var document = Document(("ITEM-1", 10m));

        var (_, error) = TransferRequestEditMapper.BuildWarehouseChange(document, "KEFBYC", null);

        Assert.Contains("to itself", error);
    }

    [Fact]
    public async Task A_depot_controller_may_assign_a_warehouse_they_run()
    {
        var user = await AddUserAsync(ApplicationRoles.DepotController, "KEFBYC", "VAN010");

        var result = await Authorizer().EnsureCanAssignWarehouseAsync(user.Id, " van010 ", default);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task A_depot_controller_may_not_assign_a_warehouse_they_do_not_run()
    {
        // The rule this whole feature turns on: the codes he may choose from are his own.
        var user = await AddUserAsync(ApplicationRoles.DepotController, "KEFBYC", "VAN010");

        var result = await Authorizer().EnsureCanAssignWarehouseAsync(user.Id, "CORMACH", default);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Forbidden, result.FirstError.Type);
    }

    [Fact]
    public async Task A_depot_controller_with_no_warehouses_may_assign_none()
    {
        var user = await AddUserAsync(ApplicationRoles.DepotController);

        var result = await Authorizer().EnsureCanAssignWarehouseAsync(user.Id, "KEFBYC", default);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task An_administrator_may_assign_any_warehouse()
    {
        var user = await AddUserAsync(ApplicationRoles.Admin);

        var result = await Authorizer().EnsureCanAssignWarehouseAsync(user.Id, "CORMACH", default);

        Assert.False(result.IsError);
    }

    // ── Reassigning through the edit endpoint ───────────

    [Fact]
    public async Task A_depot_controller_moves_a_request_onto_another_warehouse_they_run()
    {
        // He runs the source the request draws on, so this is his own paperwork: it goes straight
        // to SAP, moved onto the other warehouse he runs.
        var user = await AddUserAsync(ApplicationRoles.DepotController, "CORMACH", "VAN010");
        var sap = new RecordingSapClient(Document(("ITEM-1", 10m), ("ITEM-2", 4m)));

        var result = await Handler(sap).Handle(
            Edit(user.Id, fromWarehouse: "VAN010"), default);

        Assert.False(result.IsError);
        Assert.False(result.Value.RequiresApproval);
        Assert.Equal("VAN010", sap.LastFromWarehouse);
        Assert.Null(sap.LastToWarehouse);
    }

    [Fact]
    public async Task A_depot_controller_cannot_move_a_request_onto_a_warehouse_they_do_not_run()
    {
        var user = await AddUserAsync(ApplicationRoles.DepotController, "KEFBYC", "VAN010");
        var sap = new RecordingSapClient(Document(("ITEM-1", 10m)));

        var result = await Handler(sap).Handle(
            Edit(user.Id, toWarehouse: "CORMACH2"), default);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Forbidden, result.FirstError.Type);
        // Refused outright: neither written to SAP, nor held for someone to approve.
        Assert.Equal(0, sap.Updates);
        Assert.Empty(_context.PendingTransferRequestEdits);
    }

    [Fact]
    public async Task A_reassignment_the_editor_cannot_apply_alone_is_held_with_both_ends_recorded()
    {
        // He runs VAN010 but not the source, so the change waits for approval — and what it would
        // do to the warehouses has to survive the wait, or the approver reviews only the lines.
        var user = await AddUserAsync(ApplicationRoles.DepotController, "VAN010");
        var sap = new RecordingSapClient(Document(("ITEM-1", 10m)));

        var result = await Handler(sap).Handle(
            Edit(user.Id, toWarehouse: "VAN010", lines: [Keep(0, 8m)]), default);

        Assert.False(result.IsError);
        Assert.True(result.Value.RequiresApproval);
        Assert.Equal(0, sap.Updates);

        var held = Assert.Single(_context.PendingTransferRequestEdits);
        Assert.Null(held.ProposedFromWarehouse);
        Assert.Equal("VAN010", held.ProposedToWarehouse);
        Assert.Equal("CORMACH", held.FromWarehouse);
    }

    [Fact]
    public async Task Moving_only_the_warehouse_is_a_change_worth_making()
    {
        // The lines are sent back exactly as they stand. Without the warehouse this would be
        // rejected as a no-op, and the reassignment would be lost with it.
        var user = await AddUserAsync(ApplicationRoles.DepotController, "CORMACH", "VAN010");
        var sap = new RecordingSapClient(Document(("ITEM-1", 10m)));

        var result = await Handler(sap).Handle(
            Edit(user.Id, fromWarehouse: "VAN010", lines: [Keep(0, 10m)]), default);

        Assert.False(result.IsError);
        Assert.Equal("VAN010", sap.LastFromWarehouse);
    }

    [Fact]
    public async Task An_approved_reassignment_reaches_sap_with_its_warehouses()
    {
        var proposer = await AddUserAsync(ApplicationRoles.DepotController, "KEFBYC");
        var edit = await AddHeldEditAsync(proposer);
        edit.ProposedToWarehouse = "KEFBYC";
        edit.ProposedLinesJson = TransferRequestEditMapper.Serialize(
            TransferRequestEditMapper.FromDocument(Document(("ITEM-1", 8m))));
        await _context.SaveChangesAsync();

        var sap = new RecordingSapClient(Document(("ITEM-1", 10m)));
        var applied = await Applier(sap).ApplyAsync(edit, proposer.Id, default);

        Assert.False(applied.IsError);
        Assert.Equal("KEFBYC", sap.LastToWarehouse);
        Assert.Equal(PendingTransferRequestEditStatuses.Applied, edit.Status);
    }

    [Fact]
    public async Task An_approved_reassignment_is_not_applied_once_the_proposer_loses_the_warehouse()
    {
        // Assignments move while a change waits. Approval says the change is wanted; it does not
        // say the proposer may still name that warehouse.
        var proposer = await AddUserAsync(ApplicationRoles.DepotController, "KEFBYC");
        var edit = await AddHeldEditAsync(proposer);
        edit.ProposedToWarehouse = "VAN010";
        edit.ProposedLinesJson = TransferRequestEditMapper.Serialize(
            TransferRequestEditMapper.FromDocument(Document(("ITEM-1", 8m))));
        await _context.SaveChangesAsync();

        var sap = new RecordingSapClient(Document(("ITEM-1", 10m)));
        var applied = await Applier(sap).ApplyAsync(edit, proposer.Id, default);

        Assert.True(applied.IsError);
        Assert.Equal(0, sap.Updates);
        Assert.Equal(PendingTransferRequestEditStatuses.ApplyFailed, edit.Status);
        Assert.Contains("VAN010", edit.LastError);
    }

    // ── Approval routing for held changes ───────────────

    [Fact]
    public async Task A_held_change_routes_to_an_approval_template()
    {
        var proposer = await AddUserAsync(ApplicationRoles.DepotController, "KEFBYC");
        var edit = await AddHeldEditAsync(proposer);

        var request = await ApprovalService().EnsureRequestAsync(
            ApprovalDocumentContext.ForRequestEdit(edit), proposer.Id, default);

        Assert.Equal(ApprovalDocumentTypes.InventoryTransferRequestEdit, request.DocumentType);
        Assert.Equal(edit.Id.ToString(), request.DocumentKey);
        Assert.Equal(ApprovalRequestStatuses.Pending, request.Status);
    }

    [Fact]
    public async Task A_held_change_never_routes_to_a_stage_only_depot_controllers_authorize()
    {
        // The change is held precisely because its author is a depot controller who does not
        // run the source warehouse. Routing it to a depot-controller-only stage would let that
        // same role wave through the edit the warehouse rule just stopped.
        var service = ApprovalService();
        var stages = (await service.GetStagesAsync(default)).ToDictionary(stage => stage.Id);
        var templates = (await service.GetTemplatesAsync(default))
            .Where(template => template.DocumentType == ApprovalDocumentTypes.InventoryTransferRequestEdit)
            .ToList();

        Assert.NotEmpty(templates);
        foreach (var template in templates)
        {
            foreach (var stageId in template.StageIds)
            {
                var roles = stages[stageId].AuthorizerRoles;
                Assert.False(
                    roles.Count > 0 && roles.All(role => role == ApplicationRoles.DepotController),
                    $"Template '{template.Name}' routes to '{stages[stageId].Name}', which only depot " +
                    "controllers can authorize.");
            }
        }
    }

    [Fact]
    public async Task Every_held_change_template_has_an_eligible_authorizer()
    {
        var proposer = await AddUserAsync(ApplicationRoles.DepotController, "KEFBYC");
        var edit = await AddHeldEditAsync(proposer);

        var (_, progress) = await ApprovalService().GetProgressAsync(
            ApprovalDocumentContext.ForRequestEdit(edit), default);

        Assert.NotEmpty(progress);
        Assert.All(progress, stage =>
            Assert.True(stage.AuthorizerRoles.Count > 0 || stage.AuthorizerUserIds.Count > 0));
    }

    [Fact]
    public async Task Seeding_a_new_document_type_leaves_the_existing_ones_alone()
    {
        // Seeding bails out per document type. An install that already had transfer templates
        // must still pick up the edit templates on first use.
        var service = ApprovalService();

        var templates = await service.GetTemplatesAsync(default);

        Assert.Contains(templates, template => template.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest);
        Assert.Contains(templates, template => template.DocumentType == ApprovalDocumentTypes.InventoryTransfer);
        Assert.Contains(templates, template => template.DocumentType == ApprovalDocumentTypes.InventoryTransferRequestEdit);
    }

    // ── Helpers ─────────────────────────────────────────

    private static EditTransferRequestLineDto Keep(int lineNum, decimal quantity) =>
        new() { LineNum = lineNum, Quantity = quantity };

    private static InventoryTransferRequest Document(params (string ItemCode, decimal Quantity)[] lines) => new()
    {
        DocEntry = 501,
        DocNum = 9001,
        FromWarehouse = "CORMACH",
        ToWarehouse = "KEFBYC",
        DocumentStatus = "bost_Open",
        StockTransferLines = lines
            .Select((line, index) => new InventoryTransferRequestLine
            {
                LineNum = index,
                ItemCode = line.ItemCode,
                ItemDescription = $"{line.ItemCode} description",
                Quantity = line.Quantity,
                UoMCode = "EA"
            })
            .ToList()
    };

    private static PendingTransferRequestEditEntity HeldEdit(Guid proposerId) => new()
    {
        RequestDocEntry = 501,
        RequestDocNum = 9001,
        FromWarehouse = "CORMACH",
        ToWarehouse = "KEFBYC",
        OriginalLinesJson = "[]",
        ProposedLinesJson = "[]",
        CreatedByUserId = proposerId,
        CreatedByName = "Proposer",
        CreatedByRole = ApplicationRoles.DepotController
    };

    private async Task<PendingTransferRequestEditEntity> AddHeldEditAsync(User proposer)
    {
        var edit = HeldEdit(proposer.Id);
        _context.PendingTransferRequestEdits.Add(edit);
        await _context.SaveChangesAsync();
        return edit;
    }

    private async Task<User> AddUserAsync(string role, params string[] warehouseCodes)
    {
        var user = new User
        {
            Username = $"user-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            Role = role,
            IsActive = true
        };
        user.SetWarehouseCodes(warehouseCodes.ToList());
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private static EditTransferRequestCommand Edit(
        Guid userId,
        string? fromWarehouse = null,
        string? toWarehouse = null,
        List<EditTransferRequestLineDto>? lines = null) =>
        new(501,
            new EditTransferRequestDto
            {
                Lines = lines ?? [Keep(0, 6m)],
                FromWarehouse = fromWarehouse,
                ToWarehouse = toWarehouse,
                Reason = "Depot out of stock"
            },
            userId);

    private TransferWarehouseAuthorizer Authorizer() => new(_context);

    private InventoryTransferApprovalService ApprovalService() =>
        new(_context, new NoOpNotificationService(), NullLogger<InventoryTransferApprovalService>.Instance);

    private EditTransferRequestHandler Handler(RecordingSapClient sap) =>
        new(_context, sap.AsClient(), ApprovalService(), Authorizer(), new NoOpAuditService(),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<EditTransferRequestHandler>.Instance);

    private PendingTransferRequestEditApplier Applier(RecordingSapClient sap) =>
        new(_context, sap.AsClient(), Authorizer(), new NoOpNotificationService(), new NoOpAuditService(),
            NullLogger<PendingTransferRequestEditApplier>.Instance);

    /// <summary>
    /// Answers the two transfer-request calls an edit makes and records what was written, so a
    /// test can assert on the warehouses that reached SAP — or that nothing did.
    /// </summary>
    private sealed class RecordingSapClient(InventoryTransferRequest document)
    {
        public int Updates { get; private set; }
        public string? LastFromWarehouse { get; private set; }
        public string? LastToWarehouse { get; private set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetInventoryTransferRequestByDocEntryAsync) =>
                    (object)Task.FromResult<InventoryTransferRequest?>(document),
                nameof(ISAPServiceLayerClient.UpdateInventoryTransferRequestAsync) =>
                    Update((string?)args![2], (string?)args[3]),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<InventoryTransferRequest> Update(string? fromWarehouse, string? toWarehouse)
        {
            Updates++;
            LastFromWarehouse = fromWarehouse;
            LastToWarehouse = toWarehouse;
            return Task.FromResult(document);
        }
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
