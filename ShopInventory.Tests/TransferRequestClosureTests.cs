using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Commands.CloseTransferRequest;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers turning a transfer request down through the drawer's "Close in SAP" button.
/// </summary>
/// <remarks>
/// Closing used to record a local approval rejection and stop there, which closed nothing. Two
/// things went wrong with that. The document stayed open in SAP, so the Transfer Requests tab —
/// which lists what SAP still holds open — showed the request again straight after it was closed.
/// And a request raised directly in SAP has no approval to reject, so the decision matched only the
/// catch-all Administrator Review stage and a stock controller was refused with 403 for a document
/// this app is not the approval gate for.
/// </remarks>
public sealed class TransferRequestClosureTests : IDisposable
{
    private const int DocEntry = 501;
    private const int DocNum = 9001;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;

    public TransferRequestClosureTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── A request raised in SAP ─────────────────────────

    [Fact]
    public async Task A_request_raised_in_SAP_is_closed_in_SAP()
    {
        var stockController = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new RecordingSapClient(Document());

        var result = await Handler(sap).Handle(new CloseTransferRequestCommand(DocEntry, stockController.Id), default);

        Assert.False(result.IsError);
        Assert.Equal([DocEntry], sap.Closed);
        Assert.Contains("closed in SAP", result.Value.Message);
    }

    [Fact]
    public async Task Closing_a_request_raised_in_SAP_is_not_refused_as_an_administrator_review()
    {
        // The exact failure reported: a stock controller pressing "Close in SAP" on a SAP-raised
        // request got 403 ApprovalProcess.NotAuthorizer, because the decision was routed at an
        // approval that only existed because closing had asked for one.
        var stockController = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new RecordingSapClient(Document());

        var result = await Handler(sap).Handle(new CloseTransferRequestCommand(DocEntry, stockController.Id), default);

        Assert.False(result.IsError);
        Assert.Empty(await _context.ApprovalRequests.ToListAsync());
        Assert.Empty(await _context.Notifications.ToListAsync());
    }

    [Fact]
    public async Task A_request_SAP_already_reports_closed_is_not_closed_again()
    {
        var stockController = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new RecordingSapClient(Document(documentStatus: "bost_Close"));

        var result = await Handler(sap).Handle(new CloseTransferRequestCommand(DocEntry, stockController.Id), default);

        Assert.False(result.IsError);
        Assert.Empty(sap.Closed);
        Assert.Contains("already closed", result.Value.Message);
    }

    [Fact]
    public async Task Pressing_close_twice_closes_the_document_once()
    {
        var stockController = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new RecordingSapClient(Document());
        var command = new CloseTransferRequestCommand(DocEntry, stockController.Id);

        await Handler(sap).Handle(command, default);
        var replay = await Handler(sap).Handle(command, default);

        Assert.False(replay.IsError);
        Assert.Equal([DocEntry], sap.Closed);
    }

    [Fact]
    public async Task A_close_SAP_refuses_is_reported_rather_than_reported_as_closed()
    {
        // Silently swallowing this is how the request stayed open while the screen said otherwise.
        var stockController = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new RecordingSapClient(Document(), closeFailure: "500 - Document is already closed");

        var result = await Handler(sap).Handle(new CloseTransferRequestCommand(DocEntry, stockController.Id), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.TransferRequestCloseFailed", result.FirstError.Code);
    }

    // ── Warehouse scope survives ────────────────────────

    [Fact]
    public async Task A_depot_controller_may_not_close_a_request_out_of_another_warehouse()
    {
        var depotController = await AddUserAsync(ApplicationRoles.DepotController, "WH99");
        var sap = new RecordingSapClient(Document());

        var result = await Handler(sap).Handle(new CloseTransferRequestCommand(DocEntry, depotController.Id), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.WarehouseNotAssigned", result.FirstError.Code);
        Assert.Empty(sap.Closed);
    }

    [Fact]
    public async Task A_depot_controller_may_close_a_request_out_of_a_warehouse_they_run()
    {
        var depotController = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var sap = new RecordingSapClient(Document());

        var result = await Handler(sap).Handle(new CloseTransferRequestCommand(DocEntry, depotController.Id), default);

        Assert.False(result.IsError);
        Assert.Equal([DocEntry], sap.Closed);
    }

    // ── A request this app raised ───────────────────────

    [Fact]
    public async Task A_rejected_request_this_app_raised_is_closed_in_SAP_too()
    {
        // The rejection is only worth anything if the document stops being open: the Transfer
        // Requests tab lists what SAP still holds open, so a request left open comes right back.
        var requester = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var stockController = await AddUserAsync(ApplicationRoles.StockController);
        await OpenApprovalAsync(requester);
        var sap = new RecordingSapClient(Document());

        var result = await Handler(sap).Handle(new CloseTransferRequestCommand(DocEntry, stockController.Id), default);

        Assert.False(result.IsError);
        Assert.Equal([DocEntry], sap.Closed);
    }

    [Fact]
    public async Task The_rejection_this_app_raised_is_still_recorded_against_the_approval()
    {
        var requester = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var stockController = await AddUserAsync(ApplicationRoles.StockController);
        await OpenApprovalAsync(requester);
        var sap = new RecordingSapClient(Document());

        var result = await Handler(sap).Handle(
            new CloseTransferRequestCommand(DocEntry, stockController.Id, Remarks: "Nothing to send"), default);

        Assert.False(result.IsError);
        var stored = await _context.ApprovalRequests
            .AsNoTracking()
            .Include(request => request.Decisions)
            .SingleAsync(request =>
                request.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest &&
                request.DocumentKey == DocEntry.ToString());
        Assert.Equal(ApprovalRequestStatuses.NotApproved, stored.Status);
        var decision = Assert.Single(stored.Decisions);
        Assert.Equal(ApprovalDecisionValues.NotApproved, decision.Decision);
        Assert.Equal(stockController.Id, decision.AuthorizerUserId);
        Assert.Equal("Nothing to send", decision.Remarks);
    }

    [Fact]
    public async Task A_refusal_that_has_not_turned_the_request_down_yet_leaves_it_open()
    {
        // A stage that wants two refusals has not rejected anything after one. Closing the document
        // there would end a request the approval process is still running.
        var requester = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var first = await AddUserAsync(ApplicationRoles.StockController);
        await UseTwoRefusalStageAsync();
        await OpenApprovalAsync(requester);
        var sap = new RecordingSapClient(Document());

        var result = await Handler(sap).Handle(new CloseTransferRequestCommand(DocEntry, first.Id), default);

        Assert.False(result.IsError);
        Assert.Empty(sap.Closed);
        var stored = await _context.ApprovalRequests.AsNoTracking()
            .SingleAsync(request => request.DocumentKey == DocEntry.ToString());
        Assert.Equal(ApprovalRequestStatuses.Pending, stored.Status);
    }

    [Fact]
    public async Task A_recorded_rejection_SAP_would_not_close_is_reported_as_still_open()
    {
        var requester = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        var stockController = await AddUserAsync(ApplicationRoles.StockController);
        await OpenApprovalAsync(requester);
        var sap = new RecordingSapClient(Document(), closeFailure: "503 - Service Layer unavailable");

        var result = await Handler(sap).Handle(new CloseTransferRequestCommand(DocEntry, stockController.Id), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.TransferRequestCloseFailed", result.FirstError.Code);
        Assert.Contains("still open", result.FirstError.Description);
        // The decision stands, so sending it again retries the close rather than being refused as
        // an idempotency replay of a call that never finished.
        var stored = await _context.ApprovalRequests.AsNoTracking()
            .SingleAsync(request => request.DocumentKey == DocEntry.ToString());
        Assert.Equal(ApprovalRequestStatuses.NotApproved, stored.Status);
        Assert.Empty(await _context.IdempotencyRequests.ToListAsync());
    }

    // ── Telling the two apart ───────────────────────────

    [Fact]
    public async Task Asking_whether_a_request_has_an_approval_does_not_open_one()
    {
        var service = ApprovalService();
        var document = Document();

        var has = await service.HasApprovalAsync(ApprovalDocumentContext.ForTransferRequest(document), default);

        Assert.False(has);
        Assert.Empty(await _context.ApprovalRequests.ToListAsync());
    }

    [Fact]
    public async Task A_request_this_app_raised_is_recognised_as_having_an_approval()
    {
        var requester = await AddUserAsync(ApplicationRoles.DepotController, "WH01");
        await OpenApprovalAsync(requester);

        var has = await ApprovalService().HasApprovalAsync(
            ApprovalDocumentContext.ForTransferRequest(Document()), default);

        Assert.True(has);
    }

    // ── Helpers ─────────────────────────────────────────

    private static InventoryTransferRequest Document(string documentStatus = "bost_Open") => new()
    {
        DocEntry = DocEntry,
        DocNum = DocNum,
        FromWarehouse = "WH01",
        ToWarehouse = "WH02",
        DocumentStatus = documentStatus
    };

    private CloseTransferRequestHandler Handler(RecordingSapClient sap) =>
        new(sap.AsClient(), ApprovalService(), new TransferWarehouseAuthorizer(_context),
            new IdempotencyRequestStore(new SingleContextScopeFactory(_options), Options.Create(new SecuritySettings())),
            new NoOpAuditService(),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<CloseTransferRequestHandler>.Instance);

    private InventoryTransferApprovalService ApprovalService() =>
        new(_context, new NoOpNotificationService(), NullLogger<InventoryTransferApprovalService>.Instance);

    /// <summary>Raises the request the way this app's create path does: with an approval opened.</summary>
    private Task<ApprovalRequestEntity> OpenApprovalAsync(User requester) =>
        ApprovalService().EnsureRequestAsync(Document(), requester.Id, default);

    /// <summary>
    /// Puts a stage that wants two refusals in front of everything else, so one refusal leaves the
    /// approval running.
    /// </summary>
    private async Task UseTwoRefusalStageAsync()
    {
        var service = ApprovalService();
        var stage = await service.SaveStageAsync(new ApprovalStageDefinitionDto
        {
            Name = "Two stock officers",
            ApprovalsRequired = 1,
            RejectionsRequired = 2,
            AuthorizerRoles = [ApplicationRoles.StockController],
            IsActive = true
        }, default);
        Assert.False(stage.IsError);

        var template = await service.SaveTemplateAsync(new ApprovalTemplateDefinitionDto
        {
            Name = "Two refusals",
            DocumentType = ApprovalDocumentTypes.InventoryTransferRequest,
            StageIds = [stage.Value.Id],
            Priority = 500,
            IsActive = true
        }, default);
        Assert.False(template.IsError);
    }

    private async Task<User> AddUserAsync(string role, params string[] warehouseCodes)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"user-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "hash",
            Role = role,
            IsActive = true
        };
        user.SetWarehouseCodes(warehouseCodes.ToList());
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Answers the two transfer-request calls closing makes, and records every close that reached
    /// SAP — the point of these tests being that one has to.
    /// </summary>
    private sealed class RecordingSapClient(InventoryTransferRequest document, string? closeFailure = null)
    {
        public List<int> Closed { get; } = [];

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetInventoryTransferRequestByDocEntryAsync) =>
                    (object)Task.FromResult<InventoryTransferRequest?>(document),
                nameof(ISAPServiceLayerClient.CloseInventoryTransferRequestAsync) => Close((int)args![0]!),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task Close(int docEntry)
        {
            if (closeFailure is not null)
                return Task.FromException(new Exception($"Failed to close transfer request {docEntry}: {closeFailure}"));

            Closed.Add(docEntry);
            return Task.CompletedTask;
        }
    }

    /// <summary>The real store over its own context per scope, exactly as it runs in production.</summary>
    private sealed class SingleContextScopeFactory(DbContextOptions<ApplicationDbContext> options)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
            => serviceType == typeof(ApplicationDbContext) ? new ApplicationDbContext(options) : null;

        public void Dispose()
        {
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
