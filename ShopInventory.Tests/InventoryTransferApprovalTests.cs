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
/// Covers the approval gate on direct inventory transfers and the warehouse scoping that
/// limits which transfers a depot controller can action.
/// </summary>
/// <remarks>
/// Two things here are easy to get wrong and expensive to get wrong. First, the approval engine
/// used to serve one document type, and seeding bailed out if <em>any</em> template existed — so
/// an install that already had transfer-request templates would have silently found no template
/// for a direct transfer and thrown on first use. Second, warehouse scoping is an access rule:
/// if it stops applying, a depot controller can move stock out of a warehouse they do not run.
/// </remarks>
public sealed class InventoryTransferApprovalTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public InventoryTransferApprovalTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Production registers the context with NoTracking as the default, so a write path that
        // forgets to opt into tracking loses its changes silently. Matching that here is what
        // makes these tests able to see it.
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── Warehouse scoping ───────────────────────────────

    [Fact]
    public async Task Depot_controller_may_action_a_transfer_out_of_an_assigned_warehouse()
    {
        var user = await AddUserAsync(ApplicationRoles.DepotController, "WH01", "WH02");

        var result = await Authorizer().EnsureCanActOnSourceAsync(user.Id, "WH01", default);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Depot_controller_may_not_action_a_transfer_out_of_another_warehouse()
    {
        var user = await AddUserAsync(ApplicationRoles.DepotController, "WH01");

        var result = await Authorizer().EnsureCanActOnSourceAsync(user.Id, "WH99", default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.WarehouseNotAssigned", result.FirstError.Code);
    }

    [Fact]
    public async Task Warehouse_match_ignores_case_and_surrounding_space()
    {
        var user = await AddUserAsync(ApplicationRoles.DepotController, "WH01");

        var result = await Authorizer().EnsureCanActOnSourceAsync(user.Id, " wh01 ", default);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Depot_controller_without_any_assigned_warehouse_is_refused()
    {
        var user = await AddUserAsync(ApplicationRoles.DepotController);

        var result = await Authorizer().EnsureCanActOnSourceAsync(user.Id, "WH01", default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.NoAssignedWarehouses", result.FirstError.Code);
    }

    [Fact]
    public async Task Administrators_are_not_warehouse_scoped()
    {
        var user = await AddUserAsync(ApplicationRoles.Admin, "WH01");

        var result = await Authorizer().EnsureCanActOnSourceAsync(user.Id, "WH99", default);

        Assert.False(result.IsError);
        Assert.Null(await Authorizer().GetSourceScopeAsync(user.Id, default));
    }

    [Fact]
    public async Task Stock_controllers_are_not_warehouse_scoped()
    {
        // Only depot controllers are scoped today; widening this is a deliberate decision,
        // so pin the current boundary rather than leaving it implied.
        var user = await AddUserAsync(ApplicationRoles.StockController, "WH01");

        var result = await Authorizer().EnsureCanActOnSourceAsync(user.Id, "WH99", default);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task An_unknown_user_can_action_nothing()
    {
        var result = await Authorizer().EnsureCanActOnSourceAsync(Guid.NewGuid(), "WH01", default);

        Assert.True(result.IsError);
    }

    // ── Approval routing by document type ───────────────

    [Fact]
    public async Task A_direct_transfer_opens_an_approval_request_against_the_transfer_document_type()
    {
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);

        var request = await ApprovalService().EnsureRequestAsync(
            ApprovalDocumentContext.ForPendingTransfer(pending), submitter.Id, default);

        Assert.Equal(ApprovalDocumentTypes.InventoryTransfer, request.DocumentType);
        Assert.Equal(pending.Id.ToString(), request.DocumentKey);
        Assert.Equal(ApprovalRequestStatuses.Pending, request.Status);
        Assert.Equal(submitter.Id, request.OriginatorUserId);
        Assert.Equal("WH01", request.FromWarehouse);
    }

    [Fact]
    public async Task Templates_are_seeded_per_document_type_on_an_install_that_already_has_others()
    {
        // Reproduces the upgrade path: transfer-request templates exist, direct-transfer ones do not.
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        await ApprovalService().GetTemplatesAsync(default);
        _context.ApprovalTemplateDefinitions.RemoveRange(
            await _context.ApprovalTemplateDefinitions
                .AsTracking()
                .Where(template => template.DocumentType == ApprovalDocumentTypes.InventoryTransfer)
                .ToListAsync());
        await _context.SaveChangesAsync();
        Assert.NotEmpty(await _context.ApprovalTemplateDefinitions.ToListAsync());

        var pending = await AddPendingTransferAsync(submitter);
        var request = await ApprovalService().EnsureRequestAsync(
            ApprovalDocumentContext.ForPendingTransfer(pending), submitter.Id, default);

        Assert.Equal(ApprovalDocumentTypes.InventoryTransfer, request.DocumentType);
    }

    [Fact]
    public async Task Asking_twice_for_the_same_transfer_returns_the_same_approval_request()
    {
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);
        var context = ApprovalDocumentContext.ForPendingTransfer(pending);

        var first = await ApprovalService().EnsureRequestAsync(context, submitter.Id, default);
        var second = await ApprovalService().EnsureRequestAsync(context, submitter.Id, default);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await _context.ApprovalRequests
            .CountAsync(item => item.DocumentType == ApprovalDocumentTypes.InventoryTransfer));
    }

    [Fact]
    public async Task A_transfer_and_a_transfer_request_sharing_a_key_get_separate_approvals()
    {
        // DocumentKey is unique per document type, not globally. A transfer request keyed "1"
        // and a direct transfer keyed "1" must not collide.
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");

        var transferRequest = await ApprovalService().EnsureRequestAsync(
            new ApprovalDocumentContext(ApprovalDocumentTypes.InventoryTransferRequest, "1", "1", "WH01", "WH02"),
            submitter.Id, default);
        var directTransfer = await ApprovalService().EnsureRequestAsync(
            new ApprovalDocumentContext(ApprovalDocumentTypes.InventoryTransfer, "1", null, "WH01", "WH02"),
            submitter.Id, default);

        Assert.NotEqual(transferRequest.Id, directTransfer.Id);
    }

    [Fact]
    public async Task Only_a_stage_authorizer_may_record_a_decision()
    {
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var outsider = await AddUserAsync(ApplicationRoles.Cashier);
        var pending = await AddPendingTransferAsync(submitter);
        var context = ApprovalDocumentContext.ForPendingTransfer(pending);

        var result = await ApprovalService().SubmitDecisionAsync(
            context, outsider.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.True(result.IsError);
        Assert.Equal("ApprovalProcess.NotAuthorizer", result.FirstError.Code);
    }

    [Fact]
    public async Task A_decision_before_the_approval_exists_still_routes_by_the_submitter()
    {
        // The pending record knows who raised it, so the template must be chosen from that
        // rather than falling through to the catch-all administrator review.
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);

        var request = await ApprovalService().EnsureRequestAsync(
            ApprovalDocumentContext.ForPendingTransfer(pending), null, default);

        Assert.Equal(submitter.Id, request.OriginatorUserId);
        Assert.Equal(ApplicationRoles.DepotController, request.OriginatorRole);
        Assert.Equal("Depot Controller Direct Transfers", request.TemplateName);
    }

    [Fact]
    public async Task Approving_the_only_stage_completes_the_approval()
    {
        // A depot controller's transfer routes to the stock officer stage by default.
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var stockOfficer = await AddUserAsync(ApplicationRoles.StockController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);
        var context = ApprovalDocumentContext.ForPendingTransfer(pending);

        var result = await ApprovalService().SubmitDecisionAsync(
            context, stockOfficer.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.False(result.IsError);
        Assert.True(result.Value.ApprovalProcessComplete);
        Assert.False(result.Value.Rejected);
    }

    [Fact]
    public async Task An_approval_decision_survives_being_read_back()
    {
        // Reported from the field: DT-2026-00001 was approved and posted to SAP, yet the screen
        // kept showing it awaiting a stock officer. The decision was only ever applied in memory,
        // so the approval completed, SAP took the transfer, and nothing was written down.
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var stockOfficer = await AddUserAsync(ApplicationRoles.StockController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);
        var context = ApprovalDocumentContext.ForPendingTransfer(pending);
        await ApprovalService().EnsureRequestAsync(context, submitter.Id, default);
        StartNewRequestScope();

        await ApprovalService().SubmitDecisionAsync(
            context, stockOfficer.Id, ApprovalDecisionValues.Approved, null, "Stock confirmed", default);

        var stored = await _context.ApprovalRequests
            .AsNoTracking()
            .Include(request => request.Decisions)
            .SingleAsync(request =>
                request.DocumentType == ApprovalDocumentTypes.InventoryTransfer &&
                request.DocumentKey == pending.Id.ToString());

        Assert.Equal(ApprovalRequestStatuses.Approved, stored.Status);
        var decision = Assert.Single(stored.Decisions);
        Assert.Equal(ApprovalDecisionValues.Approved, decision.Decision);
        Assert.Equal(stockOfficer.Id, decision.AuthorizerUserId);
        Assert.Equal("Stock confirmed", decision.Remarks);
    }

    [Fact]
    public async Task A_posted_transfer_is_marked_generated_in_storage()
    {
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var stockOfficer = await AddUserAsync(ApplicationRoles.StockController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);
        var context = ApprovalDocumentContext.ForPendingTransfer(pending);
        await ApprovalService().SubmitDecisionAsync(
            context, stockOfficer.Id, ApprovalDecisionValues.Approved, null, null, default);
        StartNewRequestScope();

        await ApprovalService().MarkGeneratedAsync(
            ApprovalDocumentTypes.InventoryTransfer, pending.Id.ToString(),
            9001, 501, stockOfficer.Id, byAuthorizer: true, default);

        var stored = await _context.ApprovalRequests
            .AsNoTracking()
            .SingleAsync(request => request.DocumentKey == pending.Id.ToString());

        Assert.Equal(ApprovalRequestStatuses.GeneratedByAuthorizer, stored.Status);
        Assert.Equal(9001, stored.GeneratedDocumentEntry);
        Assert.Equal(501, stored.GeneratedDocumentNumber);
    }

    [Fact]
    public async Task Rejecting_a_stage_marks_the_approval_rejected()
    {
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var stockOfficer = await AddUserAsync(ApplicationRoles.StockController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);
        var context = ApprovalDocumentContext.ForPendingTransfer(pending);

        var result = await ApprovalService().SubmitDecisionAsync(
            context, stockOfficer.Id, ApprovalDecisionValues.NotApproved, null, "Out of stock", default);

        Assert.False(result.IsError);
        Assert.True(result.Value.Rejected);
        Assert.False(result.Value.ApprovalProcessComplete);
    }

    [Fact]
    public async Task No_default_direct_transfer_template_routes_to_a_warehouse_scoped_stage()
    {
        // Depot controllers may only action stock leaving a warehouse they run. A default template
        // that sent a direct transfer to a depot-controller-only stage would therefore be
        // unapprovable whenever the source warehouse belongs to someone else. Stock officers and
        // administrators are unscoped, so only they may appear in the seeded direct-transfer path.
        var service = ApprovalService();
        var stages = (await service.GetStagesAsync(default)).ToDictionary(stage => stage.Id);
        var templates = (await service.GetTemplatesAsync(default))
            .Where(template => template.DocumentType == ApprovalDocumentTypes.InventoryTransfer)
            .ToList();

        Assert.NotEmpty(templates);
        foreach (var template in templates)
        {
            foreach (var stageId in template.StageIds)
            {
                var roles = stages[stageId].AuthorizerRoles;
                Assert.False(
                    roles.Count > 0 && roles.All(role => role == ApplicationRoles.DepotController),
                    $"Template '{template.Name}' routes to '{stages[stageId].Name}', which only " +
                    "depot controllers can authorize — they are scoped to the source warehouse.");
            }
        }
    }

    [Fact]
    public async Task Every_default_direct_transfer_template_has_an_eligible_authorizer()
    {
        var submitter = await AddUserAsync(ApplicationRoles.StockController, "WH00");
        var pending = await AddPendingTransferAsync(submitter);

        var (_, progress) = await ApprovalService().GetProgressAsync(
            ApprovalDocumentContext.ForPendingTransfer(pending), default);

        Assert.NotEmpty(progress);
        Assert.All(progress, stage =>
            Assert.True(stage.AuthorizerRoles.Count > 0 || stage.AuthorizerUserIds.Count > 0));
    }

    [Fact]
    public async Task Templates_reject_a_document_type_the_engine_does_not_know()
    {
        var service = ApprovalService();
        var stages = await service.GetStagesAsync(default);

        var result = await service.SaveTemplateAsync(new ApprovalTemplateDefinitionDto
        {
            Name = "Purchase orders",
            DocumentType = "PurchaseOrder",
            StageIds = [stages[0].Id]
        }, default);

        Assert.True(result.IsError);
    }

    // ── Listing survives an unusable approval configuration ─

    [Fact]
    public async Task Listing_a_request_no_template_can_route_still_returns_the_request()
    {
        // Reported from the field: the Transfer Requests tab showed nothing but "No active approval
        // template is available for InventoryTransferRequest". Listing is a read — a request the
        // configuration cannot route belongs on screen without its approval progress, not in place
        // of every other request on the page.
        var service = ApprovalService();
        await service.GetTemplatesAsync(default);
        foreach (var template in await _context.ApprovalTemplateDefinitions
                     .AsTracking()
                     .Where(item => item.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest)
                     .ToListAsync())
            template.IsActive = false;
        await _context.SaveChangesAsync();

        var dto = new InventoryTransferRequestDto
        {
            DocEntry = 4021,
            DocNum = 4021,
            FromWarehouse = "WH01",
            ToWarehouse = "WH02",
            Comments = "Requester: Tendai | Email: tendai@example.test"
        };

        await service.EnrichAsync([dto], default);

        Assert.Equal("Tendai", dto.RequesterName);
        Assert.Null(dto.ApprovalStatus);
        Assert.Empty(dto.ApprovalStages);
    }

    [Fact]
    public async Task Listing_reports_approval_progress_for_requests_that_already_have_it()
    {
        var service = ApprovalService();
        var dto = new InventoryTransferRequestDto
        {
            DocEntry = 4022,
            DocNum = 4022,
            FromWarehouse = "WH01",
            ToWarehouse = "WH02"
        };

        await service.EnrichAsync([dto], default);
        var first = dto.ApprovalRequestId;
        await service.EnrichAsync([dto], default);

        Assert.NotNull(first);
        Assert.Equal(first, dto.ApprovalRequestId);
        Assert.Equal(ApprovalRequestStatuses.Pending, dto.ApprovalStatus);
        Assert.NotEmpty(dto.ApprovalStages);
        Assert.Equal(1, await _context.ApprovalRequests
            .CountAsync(item => item.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest));
    }

    // ── Remarks carried into SAP ────────────────────────

    [Fact]
    public async Task Remarks_carry_the_reason_the_requester_and_the_approver()
    {
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var stockOfficer = await AddUserAsync(ApplicationRoles.StockController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);
        pending.Comments = "Restocking the front counter";
        var context = ApprovalDocumentContext.ForPendingTransfer(pending);
        await ApprovalService().SubmitDecisionAsync(
            context, stockOfficer.Id, ApprovalDecisionValues.Approved, null, null, default);
        var (_, stages) = await ApprovalService().GetProgressAsync(context, default);

        var remarks = InventoryTransferRemarks.Build(pending, stages);

        Assert.Contains("Reason: Restocking the front counter", remarks);
        Assert.Contains($"Requested by {submitter.Username} on ", remarks);
        Assert.Contains($"Approved by {stockOfficer.Username} on ", remarks);
        Assert.Contains(AuditService.ToCAT(pending.CreatedAtUtc).ToString("dd MMM yyyy HH:mm"), remarks);
    }

    [Fact]
    public async Task Remarks_name_every_approver_in_the_order_they_signed()
    {
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);
        var first = await AddUserAsync(ApplicationRoles.StockController, "WH01");
        var second = await AddUserAsync(ApplicationRoles.Admin);
        var stages = new List<ApprovalStageProgressDto>
        {
            StageWith(
                Decision(second, new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc)),
                Decision(first, new DateTime(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc)))
        };

        var remarks = InventoryTransferRemarks.Build(pending, stages);

        Assert.Contains($"Approved by {first.Username} on ", remarks);
        Assert.True(
            remarks.IndexOf(first.Username, StringComparison.Ordinal) <
            remarks.IndexOf(second.Username, StringComparison.Ordinal),
            "Approvers should read in the order they signed.");
    }

    [Fact]
    public async Task A_rejection_is_never_reported_as_an_approval()
    {
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var refuser = await AddUserAsync(ApplicationRoles.StockController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);
        var decision = Decision(refuser, DateTime.UtcNow);
        decision.Decision = ApprovalDecisionValues.NotApproved;

        var remarks = InventoryTransferRemarks.Build(pending, [StageWith(decision)]);

        Assert.DoesNotContain("Approved by", remarks);
        Assert.DoesNotContain(refuser.Username, remarks);
    }

    [Fact]
    public async Task A_long_reason_is_trimmed_rather_than_the_audit_trail()
    {
        // SAP takes 254 characters. Losing the tail of someone's note is recoverable; losing who
        // authorised the stock movement is the whole point of writing it there.
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var approver = await AddUserAsync(ApplicationRoles.StockController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);
        pending.Comments = new string('x', 400);

        var remarks = InventoryTransferRemarks.Build(pending, [StageWith(Decision(approver, DateTime.UtcNow))]);

        Assert.True(remarks.Length <= InventoryTransferRemarks.MaxLength, $"Length was {remarks.Length}.");
        Assert.Contains($"Requested by {submitter.Username} on ", remarks);
        Assert.Contains($"Approved by {approver.Username} on ", remarks);
    }

    [Fact]
    public async Task Remarks_still_name_the_requester_when_nothing_has_been_approved_yet()
    {
        var submitter = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var pending = await AddPendingTransferAsync(submitter);

        var remarks = InventoryTransferRemarks.Build(pending, []);

        Assert.Contains($"Requested by {submitter.Username} on ", remarks);
        Assert.DoesNotContain("Approved by", remarks);
        Assert.DoesNotContain("Reason:", remarks);
    }

    // ── Seeding repairs itself ──────────────────────────

    [Fact]
    public async Task A_stage_the_install_already_holds_under_a_default_name_is_adopted()
    {
        // Stage names are unique. Seeding used to insert the built-in stages and the templates in
        // one save, so an install already holding, say, "Administrator Review" under another id
        // failed the insert and silently kept every template out — leaving the document type with
        // nothing to route to, permanently.
        var existing = new ApprovalStageDefinitionEntity
        {
            Id = Guid.NewGuid(),
            Name = "Administrator Review",
            AuthorizerRolesJson = """["Admin"]"""
        };
        _context.ApprovalStageDefinitions.Add(existing);
        await _context.SaveChangesAsync();

        var templates = (await ApprovalService().GetTemplatesAsync(default))
            .Where(template => template.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest)
            .ToList();

        var fallback = Assert.Single(templates, template => template.Name == "General Transfer Review");
        Assert.Equal([existing.Id], fallback.StageIds);
        Assert.Equal(1, await _context.ApprovalStageDefinitions.CountAsync(stage => stage.Name == existing.Name));
    }

    [Fact]
    public async Task A_default_template_that_went_missing_is_seeded_again()
    {
        var service = ApprovalService();
        await service.GetTemplatesAsync(default);
        _context.ApprovalTemplateDefinitions.RemoveRange(
            await _context.ApprovalTemplateDefinitions
                .AsTracking()
                .Where(template => template.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest)
                .ToListAsync());
        await _context.SaveChangesAsync();

        // A second service stands in for the next request: seeding is checked once per scope.
        var templates = (await ApprovalService().GetTemplatesAsync(default))
            .Where(template => template.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest)
            .ToList();

        Assert.Contains(templates, template => template.Name == "General Transfer Review");
    }

    [Fact]
    public async Task Seeding_leaves_an_administrator_deactivated_template_alone()
    {
        var service = ApprovalService();
        await service.GetTemplatesAsync(default);
        var fallback = await _context.ApprovalTemplateDefinitions
            .AsTracking()
            .SingleAsync(template => template.Name == "General Transfer Review");
        fallback.IsActive = false;
        fallback.Priority = 7;
        await _context.SaveChangesAsync();

        var templates = await ApprovalService().GetTemplatesAsync(default);

        var reloaded = Assert.Single(templates, template => template.Name == "General Transfer Review");
        Assert.False(reloaded.IsActive);
        Assert.Equal(7, reloaded.Priority);
    }

    // ── Helpers ─────────────────────────────────────────

    private ITransferWarehouseAuthorizer Authorizer() => new TransferWarehouseAuthorizer(_context);

    /// <summary>
    /// Stands in for the next HTTP request. A transfer is submitted in one request and approved in
    /// a later one, so the approval arrives at a context that has never seen the request before and
    /// has to load it from storage — which is precisely where a no-tracking load loses the decision.
    /// </summary>
    private void StartNewRequestScope() => _context.ChangeTracker.Clear();

    private IInventoryTransferApprovalService ApprovalService() => new InventoryTransferApprovalService(
        _context,
        new NoOpNotificationService(),
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

    private static ApprovalDecisionDto Decision(User authorizer, DateTime decidedAtUtc) => new()
    {
        AuthorizerUserId = authorizer.Id,
        AuthorizerName = authorizer.Username,
        AuthorizerRole = authorizer.Role,
        Decision = ApprovalDecisionValues.Approved,
        DecidedAtUtc = decidedAtUtc
    };

    private static ApprovalStageProgressDto StageWith(params ApprovalDecisionDto[] decisions) => new()
    {
        StageId = Guid.NewGuid(),
        StageName = "Stock Officer Approval",
        Decisions = [.. decisions]
    };

    private async Task<PendingInventoryTransferEntity> AddPendingTransferAsync(User submitter)
    {
        var pending = new PendingInventoryTransferEntity
        {
            FromWarehouse = "WH01",
            ToWarehouse = "WH02",
            PayloadJson = "{}",
            CreatedByUserId = submitter.Id,
            CreatedByName = submitter.Username,
            CreatedByRole = submitter.Role,
            LineCount = 1,
            TotalQuantity = 5m
        };
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();
        return pending;
    }
}
