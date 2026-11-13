using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
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
/// An administrator posts their own transfers without a second approver. What they raise has every
/// stage met as it is raised, and a direct transfer posts to SAP from the submission itself.
/// </summary>
/// <remarks>
/// The rule this replaces refused every approval from whoever raised a document. Admin decides every
/// stage of every template, so the only approver an administrator's transfer could ever have was
/// another administrator, and one working alone could not move stock at all. Everyone else is still
/// held to it; the last tests here pin that, so widening the exemption has to be done on purpose.
/// </remarks>
public sealed class AdministratorOwnTransferTests : IDisposable
{
    private static readonly string[] SapWarehouses = ["WH01", "WH02"];

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public AdministratorOwnTransferTests()
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

    // ── The approval engine ─────────────────────────────

    [Fact]
    public async Task An_administrators_transfer_is_approved_as_it_is_raised()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var document = ApprovalDocumentContext.ForPendingTransfer(await AddPendingTransferAsync(admin));

        var request = await ApprovalService().EnsureRequestAsync(document, admin.Id, default);

        Assert.Equal(ApprovalRequestStatuses.Approved, request.Status);
        Assert.NotNull(request.CompletedAtUtc);
        StartNewRequestScope();
        var (_, stages) = await ApprovalService().GetProgressAsync(document, default);
        Assert.NotEmpty(stages);
        Assert.All(stages, stage =>
        {
            Assert.Equal(ApprovalRequestStatuses.Approved, stage.Status);
            Assert.True(stage.ApprovedByAdministrator);
        });
    }

    [Fact]
    public async Task Anyone_elses_transfer_still_waits_for_a_decision()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var document = ApprovalDocumentContext.ForPendingTransfer(await AddPendingTransferAsync(officer));

        var request = await ApprovalService().EnsureRequestAsync(document, officer.Id, default);

        Assert.Equal(ApprovalRequestStatuses.Pending, request.Status);
        var (_, stages) = await ApprovalService().GetProgressAsync(document, default);
        Assert.All(stages, stage => Assert.False(stage.ApprovedByAdministrator));
    }

    [Fact]
    public async Task The_route_preview_tells_an_administrator_nobody_needs_to_approve()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var officer = await AddUserAsync(ApplicationRoles.StockController);

        var forAdmin = await ApprovalService().PreviewRouteAsync(ApprovalDocumentContext.ForPreview(
            ApprovalDocumentTypes.InventoryTransfer, ["WH01"], ["WH02"], admin.Id), default);
        var forOfficer = await ApprovalService().PreviewRouteAsync(ApprovalDocumentContext.ForPreview(
            ApprovalDocumentTypes.InventoryTransfer, ["WH01"], ["WH02"], officer.Id), default);

        Assert.True(forAdmin.ApprovedOnRaising);
        Assert.False(forOfficer.ApprovedOnRaising);
    }

    [Fact]
    public async Task An_administrators_change_to_an_approved_transfer_keeps_it_approved()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var approver = await AddUserAsync(ApplicationRoles.Admin);
        var editor = await AddUserAsync(ApplicationRoles.Admin);
        var document = ApprovalDocumentContext.ForPendingTransfer(await AddPendingTransferAsync(officer));
        var approved = await ApprovalService().SubmitDecisionAsync(
            document, approver.Id, ApprovalDecisionValues.Approved, null, null, default);
        Assert.True(approved.Value.ApprovalProcessComplete);

        StartNewRequestScope();
        var reopened = await ApprovalService().ReopenAsync(document, editor.Id, default);

        Assert.Equal(ApprovalRequestStatuses.Approved, reopened.Status);
        Assert.NotNull(reopened.CompletedAtUtc);
        Assert.Equal(editor.Id, reopened.ReopenedByUserId);
    }

    [Fact]
    public async Task Someone_elses_change_to_an_administrators_transfer_waits_and_the_administrator_may_approve_it()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var editor = await AddUserAsync(ApplicationRoles.StockController);
        var document = ApprovalDocumentContext.ForPendingTransfer(await AddPendingTransferAsync(admin));
        await ApprovalService().EnsureRequestAsync(document, admin.Id, default);

        StartNewRequestScope();
        var reopened = await ApprovalService().ReopenAsync(document, editor.Id, default);
        Assert.Equal(ApprovalRequestStatuses.Pending, reopened.Status);

        // The administrator raised it; their approval is allowed, where anyone else's of their own is not.
        StartNewRequestScope();
        var decision = await ApprovalService().SubmitDecisionAsync(
            document, admin.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.False(decision.IsError, decision.IsError ? decision.FirstError.Description : null);
        Assert.True(decision.Value.ApprovalProcessComplete);
    }

    [Fact]
    public async Task Nobody_else_may_approve_a_transfer_they_raised()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var document = ApprovalDocumentContext.ForPendingTransfer(await AddPendingTransferAsync(officer));
        await ApprovalService().EnsureRequestAsync(document, officer.Id, default);

        var decision = await ApprovalService().SubmitDecisionAsync(
            document, officer.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.True(decision.IsError);
    }

    // ── Submitting a direct transfer ────────────────────

    [Fact]
    public async Task An_administrators_direct_transfer_posts_to_SAP_on_submission()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var poster = new RecordingPoster(_context, succeed: true);

        var result = await Handler(poster).Handle(new CreateInventoryTransferCommand(Request(), admin.Id), default);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.False(result.Value.RequiresApproval);
        Assert.Equal(4321, result.Value.Transfer?.DocNum);
        Assert.Contains("#4321 posted to SAP", result.Value.Message);

        // Posted as an approved transfer, by the administrator who raised it.
        var call = Assert.Single(poster.Calls);
        Assert.Equal(PendingInventoryTransferStatuses.Approved, call.StatusWhenPosted);
        Assert.Equal(admin.Id, call.PostedBy);

        StartNewRequestScope();
        var held = await _context.PendingInventoryTransfers.AsNoTracking().SingleAsync();
        Assert.Equal(PendingInventoryTransferStatuses.Posted, held.Status);
        Assert.NotNull(held.DecidedAtUtc);
        var approval = await _context.ApprovalRequests.AsNoTracking().SingleAsync();
        Assert.Equal(ApprovalRequestStatuses.Approved, approval.Status);
    }

    [Fact]
    public async Task An_administrators_transfer_SAP_turns_away_stays_saved_to_post_again()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var poster = new RecordingPoster(_context, succeed: false);

        var result = await Handler(poster).Handle(new CreateInventoryTransferCommand(Request(), admin.Id), default);

        // Not a failed submission: the transfer exists, approved, and the answer says why SAP refused it.
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.False(result.Value.RequiresApproval);
        Assert.Null(result.Value.Transfer);
        Assert.Equal(PendingInventoryTransferStatuses.PostFailed, result.Value.PendingTransfer?.Status);
        Assert.Contains("Insufficient stock in WH01", result.Value.Message);
        Assert.Contains("post it again", result.Value.Message);
    }

    [Fact]
    public async Task A_stock_officers_direct_transfer_is_held_and_nothing_posts()
    {
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var poster = new RecordingPoster(_context, succeed: true);

        var result = await Handler(poster).Handle(new CreateInventoryTransferCommand(Request(), officer.Id), default);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.True(result.Value.RequiresApproval);
        Assert.Null(result.Value.Transfer);
        Assert.Empty(poster.Calls);
        var held = await _context.PendingInventoryTransfers.AsNoTracking().SingleAsync();
        Assert.Equal(PendingInventoryTransferStatuses.AwaitingApproval, held.Status);
    }

    // ── Helpers ─────────────────────────────────────────

    /// <summary>
    /// Stands in for the poster, recording each call and settling the record the way the real one
    /// does: Posted with the SAP numbers, or PostFailed with SAP's reason.
    /// </summary>
    private sealed class RecordingPoster(ApplicationDbContext context, bool succeed) : IPendingInventoryTransferPoster
    {
        public List<(string StatusWhenPosted, Guid PostedBy)> Calls { get; } = [];

        public async Task<ErrorOr<InventoryTransferDto>> PostAsync(
            PendingInventoryTransferEntity pending, Guid postedByUserId, bool postOnlyLinesInStock = false)
        {
            Calls.Add((pending.Status, postedByUserId));
            if (succeed)
            {
                pending.Status = PendingInventoryTransferStatuses.Posted;
                pending.SapDocEntry = 1234;
                pending.SapDocNum = 4321;
                await context.SaveChangesAsync();
                return new InventoryTransferDto { DocEntry = 1234, DocNum = 4321 };
            }

            pending.Status = PendingInventoryTransferStatuses.PostFailed;
            pending.LastError = "Insufficient stock in WH01";
            await context.SaveChangesAsync();
            return Errors.InventoryTransfer.CreationFailed("Insufficient stock in WH01");
        }
    }

    private CreateInventoryTransferHandler Handler(IPendingInventoryTransferPoster poster) => new(
        _context,
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name == nameof(ISAPServiceLayerClient.GetWarehousesAsync)
            ? Task.FromResult(SapWarehouses.Select(code => new WarehouseDto { WarehouseCode = code }).ToList())
            : null),
        StubProxy.For<IStockValidationService>((method, _) => method.Name == nameof(IStockValidationService.ValidateInventoryTransferStockAsync)
            ? Task.FromResult(new StockValidationResult())
            : null),
        ApprovalService(),
        poster,
        StubProxy.For<IAuditService>((method, _) => method.ReturnType == typeof(Task) ? Task.CompletedTask : null),
        // No ClientRequestId, so the idempotency store is never asked.
        StubProxy.Unused<IIdempotencyRequestStore>(),
        Options.Create(new SAPSettings { Enabled = true, TransferStockValidationBudgetSeconds = 5 }),
        NullLogger<CreateInventoryTransferHandler>.Instance);

    private static CreateInventoryTransferRequest Request() => new()
    {
        FromWarehouse = "WH01",
        ToWarehouse = "WH02",
        DocDate = "2026-11-13",
        Lines =
        [
            new CreateInventoryTransferLineRequest { ItemCode = "ITEM001", Quantity = 5, UoMCode = "EA" }
        ]
    };

    /// <summary>Stands in for the next HTTP request, which loads the approval from storage.</summary>
    private void StartNewRequestScope() => _context.ChangeTracker.Clear();

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
