using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers who may turn a transfer request into an SAP inventory transfer.
/// </summary>
/// <remarks>
/// Administrators and stock controllers may convert regardless of whether the request originated
/// in this app or directly in SAP. Depot controllers may act only when stock leaves one of their
/// assigned warehouses, and their action must complete approval before anything posts to SAP.
/// </remarks>
public sealed class TransferRequestConversionTests : IDisposable
{
    private const int RequestDocEntry = 4101;
    private const string Source = "KEFBYC";
    private const string Destination = "VAN010";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public TransferRequestConversionTests()
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

    // ── A request raised in SAP ─────────────────────────

    [Fact]
    public async Task An_administrator_converts_a_request_raised_in_SAP()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, admin.Id), default);

        Assert.False(result.IsError);
        Assert.Equal(1, sap.Conversions);
        Assert.NotNull(result.Value.Transfer);
    }

    [Fact]
    public async Task A_stock_officer_converts_a_request_raised_in_SAP()
    {
        // Reported from the field: the drawer offered "Convert to Transfer" and the call came back
        // "You are not an authorizer for the selected approval stage". A SAP-raised request routes
        // to the administrator stage, which is not a stage a stock officer authorizes — and the
        // request has no approval process here to authorize in the first place.
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, officer.Id), default);

        Assert.False(result.IsError);
        Assert.Equal(1, sap.Conversions);
    }

    [Fact]
    public async Task Converting_a_request_raised_in_SAP_opens_no_approval_against_it()
    {
        // Listing stopped opening approvals on SAP-raised requests; converting one must not put
        // the stub rows back, nor notify administrators about a review nobody asked for.
        var officer = await AddUserAsync(ApplicationRoles.StockController);

        var result = await Handler(new RecordingSapClient()).Handle(
            new ConvertTransferRequestCommand(RequestDocEntry, officer.Id), default);

        Assert.False(result.IsError);
        Assert.Empty(await _context.ApprovalRequests.ToListAsync());
        Assert.Empty(await _context.Notifications.ToListAsync());
    }

    [Fact]
    public async Task A_depot_controller_submits_a_SAP_request_for_approval()
    {
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Source);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, controller.Id), default);

        Assert.False(result.IsError);
        Assert.Null(result.Value.Transfer);
        Assert.Equal(0, sap.Conversions);
        var approval = Assert.Single(_context.ApprovalRequests);
        Assert.Equal("Depot Controller Transfers", approval.TemplateName);
        Assert.Equal(ApprovalRequestStatuses.Pending, approval.Status);
    }

    [Fact]
    public async Task A_stock_officer_approval_posts_a_SAP_request_submitted_by_a_depot_controller()
    {
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Source);
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new RecordingSapClient();
        var handler = Handler(sap);

        var submitted = await handler.Handle(
            new ConvertTransferRequestCommand(RequestDocEntry, controller.Id), default);
        var (_, stages) = await ApprovalService().GetProgressAsync(
            new ApprovalDocumentContext(
                ApprovalDocumentTypes.InventoryTransferRequest,
                RequestDocEntry.ToString(),
                RequestDocEntry.ToString(),
                Source,
                Destination),
            default);
        var stockStage = Assert.Single(stages);

        var approved = await handler.Handle(
            new ConvertTransferRequestCommand(
                RequestDocEntry, officer.Id, stockStage.StageId, "Approved for posting", true),
            default);

        Assert.False(submitted.IsError);
        Assert.False(approved.IsError);
        Assert.NotNull(approved.Value.Transfer);
        Assert.Equal(1, sap.Conversions);
        var approval = Assert.Single(_context.ApprovalRequests);
        Assert.Equal(ApprovalRequestStatuses.GeneratedByAuthorizer, approval.Status);
        var decision = Assert.Single(approval.Decisions);
        Assert.Equal(officer.Id, decision.AuthorizerUserId);
        Assert.Equal(ApprovalDecisionValues.Approved, decision.Decision);
    }

    [Fact]
    public async Task A_depot_controller_may_not_convert_a_request_out_of_another_warehouse()
    {
        // The destination depot is who wants the stock; issuing it is the source warehouse's call.
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Destination);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, controller.Id), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.WarehouseNotAssigned", result.FirstError.Code);
        Assert.Equal(0, sap.Conversions);
    }

    // ── A request this app raised ───────────────────────

    [Fact]
    public async Task A_stock_controller_converts_a_request_the_app_raised()
    {
        // An app approval record must not turn conversion into an approval-stage authorization
        // check for a stock controller.
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        await OpenApprovalAsync(requester);
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, officer.Id), default);

        Assert.False(result.IsError);
        Assert.Equal(1, sap.Conversions);
        var approval = Assert.Single(_context.ApprovalRequests);
        Assert.Equal(ApprovalRequestStatuses.GeneratedByAuthorizer, approval.Status);
    }

    [Fact]
    public async Task An_administrator_converts_a_request_the_app_raised()
    {
        var requester = await AddUserAsync(ApplicationRoles.DepotController, Source);
        await OpenApprovalAsync(requester);
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, admin.Id), default);

        Assert.False(result.IsError);
        Assert.Equal(1, sap.Conversions);
    }

    [Fact]
    public async Task A_depot_controller_approval_posts_an_app_request_when_it_completes_the_process()
    {
        // A stock-controller request awaits Depot Acceptance. The depot controller is allowed to
        // approve that stage for their source warehouse, and only then does the transfer post.
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        await OpenApprovalAsync(requester);
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Source);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, controller.Id), default);

        Assert.False(result.IsError);
        Assert.Equal(1, sap.Conversions);
        var approval = Assert.Single(_context.ApprovalRequests);
        Assert.Equal(ApprovalRequestStatuses.GeneratedByAuthorizer, approval.Status);
    }

    [Fact]
    public async Task A_depot_controller_cannot_post_their_own_app_request_before_stock_approval()
    {
        var requester = await AddUserAsync(ApplicationRoles.DepotController, Source);
        await OpenApprovalAsync(requester);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, requester.Id), default);

        Assert.False(result.IsError);
        Assert.Null(result.Value.Transfer);
        Assert.Equal(0, sap.Conversions);
        var approval = Assert.Single(_context.ApprovalRequests);
        Assert.Equal("Depot Controller Transfers", approval.TemplateName);
        Assert.Equal(ApprovalRequestStatuses.Pending, approval.Status);
    }

    [Fact]
    public async Task A_depot_controller_cannot_convert_an_app_request_for_an_unassigned_warehouse()
    {
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        await OpenApprovalAsync(requester);
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Destination);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, controller.Id), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.WarehouseNotAssigned", result.FirstError.Code);
        Assert.Equal(0, sap.Conversions);
        Assert.Equal(ApprovalRequestStatuses.Pending, Assert.Single(_context.ApprovalRequests).Status);
    }

    [Fact]
    public async Task A_non_converter_role_cannot_bypass_the_endpoint_role_check()
    {
        var manager = await AddUserAsync(ApplicationRoles.Manager);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, manager.Id), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.TransferRequestConverterRoleRequired", result.FirstError.Code);
        Assert.Equal(0, sap.Conversions);
    }

    // ── Helpers ─────────────────────────────────────────

    private Task<ApprovalRequestEntity> OpenApprovalAsync(User originator)
        => ApprovalService().EnsureRequestAsync(
            new ApprovalDocumentContext(
                ApprovalDocumentTypes.InventoryTransferRequest,
                RequestDocEntry.ToString(),
                RequestDocEntry.ToString(),
                Source,
                Destination),
            originator.Id,
            default);

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

    private InventoryTransferApprovalService ApprovalService() =>
        new(_context, new NoOpNotificationService(), NullLogger<InventoryTransferApprovalService>.Instance);

    private ConvertTransferRequestHandler Handler(RecordingSapClient sap) =>
        new(sap.AsClient(), ApprovalService(), new TransferWarehouseAuthorizer(_context),
            new AlwaysAcquiresStore(), new NoOpAuditService(),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<ConvertTransferRequestHandler>.Instance);

    /// <summary>
    /// Answers the two calls a conversion makes and counts the conversions, so a test can assert
    /// that nothing reached SAP as readily as that something did.
    /// </summary>
    private sealed class RecordingSapClient
    {
        public int Conversions { get; private set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetInventoryTransferRequestByDocEntryAsync) =>
                    (object)Task.FromResult<InventoryTransferRequest?>(new InventoryTransferRequest
                    {
                        DocEntry = RequestDocEntry,
                        DocNum = RequestDocEntry,
                        DocumentStatus = "bost_Open",
                        FromWarehouse = Source,
                        ToWarehouse = Destination
                    }),
                nameof(ISAPServiceLayerClient.ConvertTransferRequestToTransferAsync) => Convert(),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<InventoryTransfer> Convert()
        {
            Conversions++;
            return Task.FromResult(new InventoryTransfer
            {
                DocEntry = 7001,
                DocNum = 7001,
                FromWarehouse = Source,
                ToWarehouse = Destination
            });
        }
    }

    /// <summary>Grants every key: these tests are about the routing decision, not the replay guard.</summary>
    private sealed class AlwaysAcquiresStore : IIdempotencyRequestStore
    {
        private long _nextId = 1;

        public Task<IdempotencyAcquireResult<TResponse>> TryAcquireAsync<TResponse>(
            string scope, string key, object request, CancellationToken cancellationToken)
            => Task.FromResult(new IdempotencyAcquireResult<TResponse>(
                IdempotencyAcquireOutcome.Acquired, _nextId++));

        public Task CompleteAsync<TResponse>(long requestId, TResponse response, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ReleaseAsync(long requestId, CancellationToken cancellationToken) => Task.CompletedTask;
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
