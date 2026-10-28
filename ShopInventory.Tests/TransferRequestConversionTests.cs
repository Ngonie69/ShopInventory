using ShopInventory.Common.Stock;
﻿using Microsoft.Data.Sqlite;
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
/// Administrators, stock controllers and the wash bay may convert from any warehouse; depot
/// controllers only when stock leaves one of theirs. Whoever it is, nothing posts until the
/// request's approval is complete, and nobody approves a request they raised.
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
    public async Task A_stock_officer_cannot_convert_a_request_raised_in_SAP_before_it_is_approved()
    {
        // Stock officers used to convert straight past the approval. A request raised in SAP has
        // nobody on file who raised it, so it routes to Administrator Review, which they do not hold.
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, officer.Id), default);

        Assert.False(result.IsError, string.Join("; ", result.Errors.Select(error => error.Description)));
        Assert.Null(result.Value.Transfer);
        Assert.Contains("awaiting Administrator Review", result.Value.Message);
        Assert.Equal(0, sap.Conversions);
        Assert.Empty(sap.LookedUp);
    }

    [Fact]
    public async Task The_wash_bay_waits_for_approval_as_a_stock_officer_does()
    {
        // No warehouses assigned: the wash bay is unscoped, so an empty assignment must not read as
        // "no warehouse you may act on" — it reaches the approval like anyone else, and stops there.
        var washBay = await AddUserAsync(ApplicationRoles.WashBay);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, washBay.Id), default);

        Assert.False(result.IsError, string.Join("; ", result.Errors.Select(error => error.Description)));
        Assert.Null(result.Value.Transfer);
        Assert.Equal(0, sap.Conversions);
    }

    [Fact]
    public async Task Converting_a_request_raised_in_SAP_opens_its_approval_routed_by_nobody_converting_it()
    {
        // The approval is opened by the first conversion, not by listing. The person converting is
        // never stamped as the one who raised it: that would route the request by their role and
        // then bar them, of all people, from approving it.
        var officer = await AddUserAsync(ApplicationRoles.StockController);

        await Handler(new RecordingSapClient()).Handle(
            new ConvertTransferRequestCommand(RequestDocEntry, officer.Id), default);

        var approval = Assert.Single(await _context.ApprovalRequests.ToListAsync());
        Assert.Equal("General Transfer Review", approval.TemplateName);
        Assert.Null(approval.OriginatorUserId);
        Assert.Equal(ApprovalRequestStatuses.Pending, approval.Status);
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
        Assert.Equal("General Transfer Review", approval.TemplateName);
        Assert.Equal(ApprovalRequestStatuses.Pending, approval.Status);
    }

    [Fact]
    public async Task An_administrator_approval_posts_a_SAP_request_a_depot_controller_submitted()
    {
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Source);
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var sap = new RecordingSapClient();
        var handler = Handler(sap);

        var submitted = await handler.Handle(
            new ConvertTransferRequestCommand(RequestDocEntry, controller.Id), default);
        var stage = Assert.Single((await ApprovalService().GetProgressAsync(RequestContext(), default)).Stages);

        var approved = await handler.Handle(
            new ConvertTransferRequestCommand(
                RequestDocEntry, admin.Id, stage.StageId, "Approved for posting", true),
            default);

        Assert.False(submitted.IsError);
        Assert.False(approved.IsError);
        Assert.NotNull(approved.Value.Transfer);
        Assert.Equal(1, sap.Conversions);
        var approval = Assert.Single(_context.ApprovalRequests);
        Assert.Equal(ApprovalRequestStatuses.GeneratedByAuthorizer, approval.Status);
        var decision = Assert.Single(approval.Decisions);
        Assert.Equal(admin.Id, decision.AuthorizerUserId);
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

    // ── What the van is told ───────────────────────

    /// <summary>
    /// The conversion is the moment a rep's request becomes stock on their van, and the one event of
    /// the day the handset cannot discover for itself.
    /// </summary>
    /// <remarks>
    /// SAP holds the link on the transfer line as BaseEntry and neither the $select nor the DTO
    /// carries it, so all the handset can otherwise do is parse the sentence the conversion writes
    /// into the transfer's remarks. This asserts the pair travels as fields instead — reword that
    /// sentence and the parse goes quiet, with every request reading as outstanding while its stock
    /// is already on the van.
    /// </remarks>
    [Fact]
    public async Task Converting_a_request_tells_the_rep_who_raised_it()
    {
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        await OpenApprovalAsync(requester);
        var admin = await AddUserAsync(ApplicationRoles.Admin);

        var result = await Handler(new RecordingSapClient())
            .Handle(new ConvertTransferRequestCommand(RequestDocEntry, admin.Id), default);

        Assert.False(result.IsError);
        var published = Assert.Single(_published.Sent);

        // Addressed, not broadcast. Category "InventoryTransfer" resolves to the inventory audience,
        // which is Admin, StockController and DepotController — a van's own roles, ADR and Sales, are
        // in none of them, so a broadcast would reach every desk and no handset.
        Assert.Equal(requester.Id, published.TargetUserId);

        // The transfer, because that is the document carrying what actually moved.
        Assert.Equal("InventoryTransfer", published.EntityType);
        Assert.Equal(published.Data?["transferDocEntry"], published.EntityId);

        // The link itself, which is the whole point.
        Assert.Equal(RequestDocEntry.ToString(), published.Data?["requestDocEntry"]);
        Assert.Contains("requestDocNum", published.Data!.Keys);
    }

    /// <summary>
    /// A request raised straight into SAP has nobody on file who asked for it, and inventing a
    /// recipient would be worse than staying quiet — the obvious wrong guess is the person doing the
    /// converting, who is standing at the depot and already knows.
    /// </summary>
    [Fact]
    public async Task Converting_a_request_raised_in_SAP_tells_nobody()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);

        var result = await Handler(new RecordingSapClient())
            .Handle(new ConvertTransferRequestCommand(RequestDocEntry, admin.Id), default);

        Assert.False(result.IsError);
        Assert.Empty(_published.Sent);
    }

    // ── A request this app raised ───────────────────────

    [Fact]
    public async Task A_stock_controller_cannot_convert_an_app_request_still_awaiting_depot_acceptance()
    {
        // The bypass this replaces: a stock controller's Convert posted the transfer while its
        // approval still sat waiting on the depot.
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        await OpenApprovalAsync(requester);
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, officer.Id), default);

        Assert.False(result.IsError);
        Assert.Null(result.Value.Transfer);
        Assert.Contains("awaiting Depot Acceptance", result.Value.Message);
        Assert.Equal(0, sap.Conversions);
        Assert.Equal(ApprovalRequestStatuses.Pending, Assert.Single(_context.ApprovalRequests).Status);
    }

    [Fact]
    public async Task Once_approved_a_request_is_converted_without_a_new_decision()
    {
        // The depot accepts it from the approvals list without generating; the stock controller who
        // raised it then converts it. Generating carries out someone else's approval, so it is
        // theirs to do, and no decision of theirs is recorded.
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        await OpenApprovalAsync(requester);
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Source);
        var sap = new RecordingSapClient();
        var stage = Assert.Single((await ApprovalService().GetProgressAsync(RequestContext(), default)).Stages);

        var accepted = await Handler(sap).Handle(
            new ConvertTransferRequestCommand(RequestDocEntry, controller.Id, stage.StageId, null, false), default);
        var converted = await Handler(sap).Handle(
            new ConvertTransferRequestCommand(RequestDocEntry, requester.Id), default);

        Assert.False(accepted.IsError);
        Assert.False(converted.IsError, string.Join("; ", converted.Errors.Select(error => error.Description)));
        Assert.NotNull(converted.Value.Transfer);
        Assert.Equal(1, sap.Conversions);
        var approval = Assert.Single(_context.ApprovalRequests.Include(item => item.Decisions));
        Assert.Equal(ApprovalRequestStatuses.Generated, approval.Status);
        Assert.Equal(controller.Id, Assert.Single(approval.Decisions).AuthorizerUserId);
    }

    [Fact]
    public async Task The_person_who_raised_a_request_cannot_approve_it_by_converting()
    {
        // Admin holds every stage, so without this an administrator could raise a request and post
        // it with nobody else looking.
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        await OpenApprovalAsync(admin);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, admin.Id), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.SelfApprovalNotAllowed", result.FirstError.Code);
        Assert.Equal(0, sap.Conversions);
        Assert.Empty(Assert.Single(_context.ApprovalRequests.Include(item => item.Decisions)).Decisions);
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

    // ── A request that was converted before ─────────────

    [Fact]
    public async Task A_request_whose_transfer_SAP_already_has_is_closed_and_not_converted_again()
    {
        // The first conversion posted and then failed to close the request, so it was still open
        // in SAP with its approval already marked generated.
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        await OpenApprovalAsync(requester);
        await ApprovalService().MarkGeneratedAsync(RequestDocEntry, 6900, 6900, requester.Id, true, default);
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var sap = new RecordingSapClient
        {
            InSap = new InventoryTransfer { DocEntry = 6900, DocNum = 6900, FromWarehouse = Source, ToWarehouse = Destination }
        };

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, officer.Id), default);

        Assert.False(result.IsError, string.Join("; ", result.Errors.Select(error => error.Description)));
        Assert.Equal(0, sap.Conversions);
        Assert.Equal(1, sap.Closes);
        Assert.Equal(6900, result.Value.Transfer!.DocEntry);
        Assert.Contains("already been converted", result.Value.Message);
    }

    [Fact]
    public async Task A_conversion_looks_for_the_request_marker_before_posting()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var sap = new RecordingSapClient();

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, admin.Id), default);

        Assert.False(result.IsError);
        Assert.Equal([$"ITR-{RequestDocEntry}"], sap.LookedUp);
        Assert.Equal(1, sap.Conversions);
    }

    [Fact]
    public async Task Nothing_is_converted_when_SAP_cannot_be_checked_for_an_earlier_conversion()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var sap = new RecordingSapClient { LookupFailure = new HttpRequestException("SAP is down") };

        var result = await Handler(sap).Handle(new ConvertTransferRequestCommand(RequestDocEntry, admin.Id), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.SapConnectionError", result.FirstError.Code);
        Assert.Equal(0, sap.Conversions);
    }

    [Fact]
    public async Task Two_people_converting_one_request_contend_for_one_lock()
    {
        // The older guards were keyed on the person, so a stock officer and an administrator
        // converting the same request each held their own lock and could both post. Both reach the
        // lock only once the request is approved, and SAP cannot be searched here, so neither posts
        // and the approval stays open for the second.
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        await OpenApprovalAsync(requester);
        var controller = await AddUserAsync(ApplicationRoles.DepotController, Source);
        var stage = Assert.Single((await ApprovalService().GetProgressAsync(RequestContext(), default)).Stages);
        await Handler(new RecordingSapClient()).Handle(
            new ConvertTransferRequestCommand(RequestDocEntry, controller.Id, stage.StageId, null, false), default);
        var officer = await AddUserAsync(ApplicationRoles.StockController);
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var store = new AlwaysAcquiresStore();
        var unreachable = new HttpRequestException("SAP is down");

        await Handler(new RecordingSapClient { LookupFailure = unreachable }, store).Handle(new ConvertTransferRequestCommand(RequestDocEntry, officer.Id), default);
        await Handler(new RecordingSapClient { LookupFailure = unreachable }, store).Handle(new ConvertTransferRequestCommand(RequestDocEntry, admin.Id), default);

        Assert.Equal(
            [RequestDocEntry.ToString(), RequestDocEntry.ToString()],
            store.KeysFor(ConvertTransferRequestHandler.ConversionPostScope));
    }

    [Fact]
    public async Task A_conversion_already_running_is_refused_without_reaching_SAP()
    {
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var sap = new RecordingSapClient();
        var store = new AlwaysAcquiresStore { HeldScope = ConvertTransferRequestHandler.ConversionPostScope };

        var result = await Handler(sap, store).Handle(new ConvertTransferRequestCommand(RequestDocEntry, admin.Id), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.ConversionInProgress", result.FirstError.Code);
        Assert.Empty(sap.LookedUp);
        Assert.Equal(0, sap.Conversions);
    }

    // ── Helpers ─────────────────────────────────────────

    private Task<ApprovalRequestEntity> OpenApprovalAsync(User originator)
        => ApprovalService().EnsureRequestAsync(RequestContext(), originator.Id, default);

    private static ApprovalDocumentContext RequestContext() => new(
        ApprovalDocumentTypes.InventoryTransferRequest,
        RequestDocEntry.ToString(),
        RequestDocEntry.ToString(),
        Source,
        Destination);

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

    /// <summary>
    /// What the handler published, kept apart from what the approval engine publishes on its own so
    /// an assertion on one is never satisfied by the other.
    /// </summary>
    private readonly NoOpNotificationService _published = new();

    private ConvertTransferRequestHandler Handler(RecordingSapClient sap, AlwaysAcquiresStore? store = null) =>
        new(sap.AsClient(), ApprovalService(), new TransferWarehouseAuthorizer(_context),
            store ?? new AlwaysAcquiresStore(), new NoOpAuditService(), _published,
            // No till has sold from Source, so the stock check must not even be asked.
            StubProxy.Unused<IStockValidationService>(),
            new UnpostedTillClaims(_context, Options.Create(new DailyStockSettings()), NullLogger<UnpostedTillClaims>.Instance),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<ConvertTransferRequestHandler>.Instance);

    /// <summary>
    /// Answers the two calls a conversion makes and counts the conversions, so a test can assert
    /// that nothing reached SAP as readily as that something did.
    /// </summary>
    private sealed class RecordingSapClient
    {
        public int Conversions { get; private set; }
        public int Closes { get; private set; }

        /// <summary>What the lookup by order number finds: a transfer an earlier conversion made.</summary>
        public InventoryTransfer? InSap { get; init; }
        public Exception? LookupFailure { get; init; }
        public List<string> LookedUp { get; } = [];

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.FindStockTransferByOrderNumberAsync) => Find((string)args![0]!),
                nameof(ISAPServiceLayerClient.CloseInventoryTransferRequestAsync) => Close(),
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

        private Task<InventoryTransfer?> Find(string orderNumber)
        {
            LookedUp.Add(orderNumber);
            return LookupFailure is null
                ? Task.FromResult(InSap)
                : Task.FromException<InventoryTransfer?>(LookupFailure);
        }

        private Task Close()
        {
            Closes++;
            return Task.CompletedTask;
        }

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

    /// <summary>
    /// Grants every key, except in <see cref="HeldScope"/>, which reads as someone else's lock. Most
    /// tests are about the routing decision, not the guards; the ones that are read the keys asked for.
    /// </summary>
    private sealed class AlwaysAcquiresStore : IIdempotencyRequestStore
    {
        private long _nextId = 1;
        private readonly List<(string Scope, string Key)> _asked = [];

        public string? HeldScope { get; init; }

        public List<string> KeysFor(string scope) =>
            _asked.Where(entry => entry.Scope == scope).Select(entry => entry.Key).ToList();

        public Task<IdempotencyAcquireResult<TResponse>> TryAcquireAsync<TResponse>(
            string scope, string key, object request, CancellationToken cancellationToken)
        {
            _asked.Add((scope, key));
            return Task.FromResult(scope == HeldScope
                ? new IdempotencyAcquireResult<TResponse>(IdempotencyAcquireOutcome.InProgress)
                : new IdempotencyAcquireResult<TResponse>(IdempotencyAcquireOutcome.Acquired, _nextId++));
        }

        public Task<IdempotencyAcquireResult<TResponse>> TryAcquireAsync<TResponse>(
            string scope, string key, object request, TimeSpan? inProgressLease, CancellationToken cancellationToken)
            => TryAcquireAsync<TResponse>(scope, key, request, cancellationToken);

        public Task<bool> RenewAsync(long requestId, TimeSpan lease, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task CompleteAsync<TResponse>(long requestId, TResponse response, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ReleaseAsync(long requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        // Nothing here ever leaves a claim standing, so nothing has one to take over.
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
