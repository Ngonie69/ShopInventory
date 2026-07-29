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

    private TransferWarehouseAuthorizer Authorizer() => new(_context);

    private InventoryTransferApprovalService ApprovalService() =>
        new(_context, new NoOpNotificationService(), NullLogger<InventoryTransferApprovalService>.Instance);
}
