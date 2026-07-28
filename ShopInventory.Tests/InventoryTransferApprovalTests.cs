using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
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

    // ── Helpers ─────────────────────────────────────────

    private ITransferWarehouseAuthorizer Authorizer() => new TransferWarehouseAuthorizer(_context);

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

    private sealed class NoOpNotificationService : INotificationService
    {
        public Task<NotificationDto> CreateNotificationAsync(CreateNotificationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new NotificationDto());

        public Task<NotificationListResponseDto> GetNotificationsAsync(string? username, IReadOnlyCollection<string>? roles, int page = 1, int pageSize = 20, bool unreadOnly = false, string? category = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new NotificationListResponseDto());

        public Task<int> GetUnreadCountAsync(string? username, IReadOnlyCollection<string>? roles, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task MarkAsReadAsync(string? username, IReadOnlyCollection<string>? roles, List<int>? notificationIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteNotificationAsync(int id, string? username, IReadOnlyCollection<string>? roles, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CleanupExpiredNotificationsAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CreateLowStockAlertAsync(string itemCode, string itemName, decimal currentStock, decimal reorderLevel, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CreateSystemAlertAsync(string title, string message, string type = "Info", CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CreateSalesOrderNotificationAsync(int orderId, string orderNumber, string customerCode, string customerName, decimal docTotal, string status, string source, string? createdByUsername, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
