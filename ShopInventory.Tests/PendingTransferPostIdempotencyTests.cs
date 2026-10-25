using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A large transfer can spend minutes inside SAP, and for all of that time the record still reads
/// Approved and the page still offers "Post to SAP" — so the button gets pressed again. Nothing
/// about the second press is distinguishable from the first, and both would create a document.
/// </summary>
public sealed class PendingTransferPostIdempotencyTests : IDisposable
{
    private static readonly Guid Poster = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherPoster = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;

    public PendingTransferPostIdempotencyTests()
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

    [Fact]
    public async Task A_second_post_while_the_first_is_in_flight_is_refused()
    {
        var pending = await GivenApprovedTransferAsync();
        var sap = new BlockingSapClient();
        var store = BuildStore();

        // Hold the first post inside SAP, exactly where the six-minute wait happened.
        var first = Poster1().PostAsync(pending, Poster);
        await sap.EnteredSap.Task;

        var second = await Poster2().PostAsync(await ReloadAsync(pending.Id), OtherPoster);

        Assert.True(second.IsError);
        Assert.Equal("InventoryTransfer.PostInProgress", second.FirstError.Code);

        sap.ReleaseSap.SetResult();
        var completed = await first;

        Assert.False(completed.IsError);
        Assert.Equal(1, sap.TransfersCreated);

        IPendingInventoryTransferPoster Poster1() => BuildPoster(_context, sap, store);
        IPendingInventoryTransferPoster Poster2() => BuildPoster(NewContext(), sap, store);
    }

    [Fact]
    public async Task A_post_that_already_succeeded_replays_the_transfer_instead_of_posting_again()
    {
        var pending = await GivenApprovedTransferAsync();
        var sap = new BlockingSapClient();
        sap.ReleaseSap.SetResult();
        var store = BuildStore();

        var first = await BuildPoster(_context, sap, store).PostAsync(pending, Poster);
        Assert.False(first.IsError);

        // The replay must carry the document, not a bare "already done" — that is the whole point
        // of the handler owning this rather than the middleware.
        var replay = await BuildPoster(NewContext(), sap, store).PostAsync(await ReloadAsync(pending.Id), Poster);

        Assert.False(replay.IsError);
        Assert.Equal(first.Value.DocEntry, replay.Value.DocEntry);
        Assert.Equal(1, sap.TransfersCreated);
    }

    [Fact]
    public async Task A_failed_post_releases_the_key_so_it_can_be_retried()
    {
        var pending = await GivenApprovedTransferAsync();
        var store = BuildStore();

        var failing = new BlockingSapClient { Failure = new InvalidOperationException("SAP said no") };
        failing.ReleaseSap.SetResult();
        var first = await BuildPoster(_context, failing, store).PostAsync(pending, Poster);

        Assert.True(first.IsError);
        Assert.Equal(PendingInventoryTransferStatuses.PostFailed, (await ReloadAsync(pending.Id)).Status);

        // A refusal created nothing, so the next attempt must not be told one is already running.
        var succeeding = new BlockingSapClient();
        succeeding.ReleaseSap.SetResult();
        var retry = await BuildPoster(NewContext(), succeeding, store).PostAsync(await ReloadAsync(pending.Id), Poster);

        Assert.False(retry.IsError);
        Assert.Equal(1, succeeding.TransfersCreated);
    }

    [Fact]
    public async Task A_failed_post_still_records_when_it_was_attempted()
    {
        var pending = await GivenApprovedTransferAsync();
        Assert.Null(pending.LastAttemptedAtUtc);

        var before = DateTime.UtcNow;
        var failing = new BlockingSapClient { Failure = new InvalidOperationException("SAP said no") };
        failing.ReleaseSap.SetResult();

        var result = await BuildPoster(_context, failing, BuildStore()).PostAsync(pending, Poster);
        Assert.True(result.IsError);

        // "Failed to post" already says an attempt happened; only the stamp says when, which is
        // what separates a failure someone is still working on from one nobody has touched for a
        // week. Read through a fresh context so this proves the column was written, not tracked.
        await using var verify = NewContext();
        var stored = await verify.PendingInventoryTransfers
            .AsNoTracking()
            .FirstAsync(item => item.Id == pending.Id);

        Assert.NotNull(stored.LastAttemptedAtUtc);
        Assert.InRange(stored.LastAttemptedAtUtc.Value, before, DateTime.UtcNow);
    }

    private async Task<PendingInventoryTransferEntity> GivenApprovedTransferAsync()
    {
        var pending = new PendingInventoryTransferEntity
        {
            Id = Guid.NewGuid(),
            DraftNumber = "DT-2026-00012",
            FromWarehouse = "KEFBYC",
            ToWarehouse = "VAN011",
            Status = PendingInventoryTransferStatuses.Approved,
            CreatedByUserId = Poster,
            CreatedByName = "Bulawayo Controller",
            CreatedByRole = ApplicationRoles.DepotController,
            CreatedAtUtc = DateTime.UtcNow,
            DecidedAtUtc = DateTime.UtcNow,
            LineCount = 1,
            TotalQuantity = 1,
            PayloadJson = PendingInventoryTransferMapper.SerializePayload(new CreateInventoryTransferRequest
            {
                FromWarehouse = "KEFBYC",
                ToWarehouse = "VAN011",
                Lines =
                [
                    new CreateInventoryTransferLineRequest
                    {
                        ItemCode = "YOG028",
                        Quantity = 1,
                        UoMCode = "EA",
                        FromWarehouseCode = "KEFBYC",
                        ToWarehouseCode = "VAN011"
                    }
                ]
            })
        };

        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return await ReloadAsync(pending.Id);
    }

    private async Task<PendingInventoryTransferEntity> ReloadAsync(Guid id)
        => await _context.PendingInventoryTransfers.AsTracking().FirstAsync(item => item.Id == id);

    private ApplicationDbContext NewContext() => new(_options);

    private IIdempotencyRequestStore BuildStore()
        => new IdempotencyRequestStore(
            new SingleContextScopeFactory(_options),
            Options.Create(new SecuritySettings()));

    private static IPendingInventoryTransferPoster BuildPoster(
        ApplicationDbContext context,
        BlockingSapClient sap,
        IIdempotencyRequestStore store)
        => new PendingInventoryTransferPoster(
            context,
            sap.AsClient(),
            StubProxy.For<IStockValidationService>((method, _) => method.Name switch
            {
                nameof(IStockValidationService.ValidateInventoryTransferStockAsync) =>
                    (object)Task.FromResult(StockValidationResult.Success()),
                _ => throw new InvalidOperationException($"Unexpected stock-validation call: {method.Name}")
            }),
            StubProxy.For<IInventoryTransferApprovalService>((method, _) => method.Name switch
            {
                // The poster tolerates a failure here — the remarks lose the approver names, not
                // the post — so an empty progress read is the honest stub.
                nameof(IInventoryTransferApprovalService.GetProgressAsync) =>
                    throw new InvalidOperationException("no approval request in this fixture"),
                nameof(IInventoryTransferApprovalService.MarkGeneratedAsync) => (object)Task.CompletedTask,
                _ => throw new InvalidOperationException($"Unexpected approval call: {method.Name}")
            }),
            new NoOpNotificationService(),
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            store,
            NullLogger<PendingInventoryTransferPoster>.Instance);

    // ── A retry after SAP did not answer ─────────────────────────

    [Fact]
    public async Task A_transfer_carries_its_draft_number_into_SAP_and_the_first_post_looks_nothing_up()
    {
        var pending = await GivenApprovedTransferAsync();
        var sap = new BlockingSapClient();
        sap.ReleaseSap.SetResult();

        var result = await BuildPoster(_context, sap, BuildStore()).PostAsync(pending, Poster);

        Assert.False(result.IsError);
        Assert.Equal("DT-2026-00012", sap.LastPosted!.OrderNumber);
        Assert.Empty(sap.LookedUp);
    }

    [Fact]
    public async Task A_retry_after_a_timeout_records_the_transfer_SAP_already_has_instead_of_posting_it_again()
    {
        var pending = await GivenApprovedTransferAsync();
        var store = BuildStore();

        // SAP took the post and the reply never came back.
        var timedOut = new BlockingSapClient { Failure = new OperationCanceledException("SAP client timeout") };
        timedOut.ReleaseSap.SetResult();
        var first = await BuildPoster(_context, timedOut, store).PostAsync(pending, Poster);
        Assert.True(first.IsError);
        var failed = await ReloadAsync(pending.Id);
        Assert.Equal(PendingTransferFailureKinds.OutcomeUnknown, PendingTransferFailureClassifier.Classify(failed.LastError).Kind);

        // The document is there, under the number the first attempt stamped on it.
        var retrying = new BlockingSapClient
        {
            InSap = new InventoryTransfer { DocEntry = 7310, DocNum = 66120, FromWarehouse = "KEFBYC", ToWarehouse = "VAN011" }
        };
        retrying.ReleaseSap.SetResult();
        var retry = await RetryHandler(NewContext(), retrying, store).Handle(
            new ShopInventory.Features.InventoryTransfers.Commands.RetryPendingTransferPost.RetryPendingTransferPostCommand(pending.Id, await AdminIdAsync()),
            default);

        Assert.False(retry.IsError, string.Join("; ", retry.Errors.Select(error => error.Description)));
        Assert.Equal(0, retrying.TransfersCreated);
        Assert.Equal(["DT-2026-00012"], retrying.LookedUp);

        await using var verify = NewContext();
        var stored = await verify.PendingInventoryTransfers.AsNoTracking().FirstAsync(item => item.Id == pending.Id);
        Assert.Equal(PendingInventoryTransferStatuses.Posted, stored.Status);
        Assert.Equal(7310, stored.SapDocEntry);
        Assert.Equal(66120, stored.SapDocNum);
        Assert.Null(stored.LastError);
    }

    [Fact]
    public async Task A_retry_after_a_timeout_posts_once_when_SAP_does_not_have_the_transfer()
    {
        var pending = await GivenApprovedTransferAsync();
        var store = BuildStore();

        var timedOut = new BlockingSapClient { Failure = new OperationCanceledException("SAP client timeout") };
        timedOut.ReleaseSap.SetResult();
        await BuildPoster(_context, timedOut, store).PostAsync(pending, Poster);

        var retrying = new BlockingSapClient();
        retrying.ReleaseSap.SetResult();
        var retry = await BuildPoster(NewContext(), retrying, store).PostAsync(await ReloadAsync(pending.Id), Poster);

        Assert.False(retry.IsError);
        Assert.Single(retrying.LookedUp);
        Assert.Equal(1, retrying.TransfersCreated);
    }

    [Fact]
    public async Task A_lookup_that_fails_posts_nothing_and_keeps_the_timeout_on_record()
    {
        var pending = await GivenApprovedTransferAsync();
        var store = BuildStore();

        var timedOut = new BlockingSapClient { Failure = new OperationCanceledException("SAP client timeout") };
        timedOut.ReleaseSap.SetResult();
        await BuildPoster(_context, timedOut, store).PostAsync(pending, Poster);
        var timeoutError = (await ReloadAsync(pending.Id)).LastError;

        var unreachable = new BlockingSapClient { LookupFailure = new HttpRequestException("SAP is down") };
        unreachable.ReleaseSap.SetResult();
        var retry = await BuildPoster(NewContext(), unreachable, store).PostAsync(await ReloadAsync(pending.Id), Poster);

        Assert.True(retry.IsError);
        Assert.Equal("InventoryTransfer.SapConnectionError", retry.FirstError.Code);
        Assert.Equal(0, unreachable.TransfersCreated);

        // Whoever looks next must still read "SAP may already have this", not a new, milder error.
        await using var verify = NewContext();
        var stored = await verify.PendingInventoryTransfers.AsNoTracking().FirstAsync(item => item.Id == pending.Id);
        Assert.Equal(timeoutError, stored.LastError);
        Assert.Equal(PendingInventoryTransferStatuses.PostFailed, stored.Status);
    }

    [Fact]
    public async Task A_timeout_recorded_before_posts_were_numbered_is_not_posted_blind()
    {
        var pending = await GivenApprovedTransferAsync();

        // As the poster wrote it before this change: SAP was never told the draft number.
        pending.Status = PendingInventoryTransferStatuses.PostFailed;
        pending.LastAttemptedAtUtc = DateTime.UtcNow.AddHours(-3);
        pending.LastError =
            "The SAP post timed out before SAP answered, so it is not known whether the transfer was created. "
            + "Check SAP for this transfer before retrying — retrying will post it again.";
        await _context.SaveChangesAsync();

        var sap = new BlockingSapClient();
        sap.ReleaseSap.SetResult();
        var retry = await BuildPoster(NewContext(), sap, BuildStore()).PostAsync(await ReloadAsync(pending.Id), Poster);

        Assert.True(retry.IsError);
        Assert.Equal("InventoryTransfer.PostOutcomeUnsearchable", retry.FirstError.Code);
        Assert.Empty(sap.LookedUp);
        Assert.Equal(0, sap.TransfersCreated);
    }

    private async Task<Guid> AdminIdAsync()
    {
        var admin = new User
        {
            Username = $"admin-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            Role = ApplicationRoles.Admin,
            IsActive = true
        };
        await using var context = NewContext();
        context.Users.Add(admin);
        await context.SaveChangesAsync();
        return admin.Id;
    }

    private static ShopInventory.Features.InventoryTransfers.Commands.RetryPendingTransferPost.RetryPendingTransferPostHandler RetryHandler(
        ApplicationDbContext context,
        BlockingSapClient sap,
        IIdempotencyRequestStore store)
        => new(
            context,
            new TransferWarehouseAuthorizer(context),
            BuildPoster(context, sap, store),
            Options.Create(new SAPSettings { Enabled = true }));

    /// <summary>Holds the SAP create open so a second post can arrive mid-flight.</summary>
    private sealed class BlockingSapClient
    {
        public TaskCompletionSource EnteredSap { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSap { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; init; }
        public int TransfersCreated { get; private set; }
        public CreateInventoryTransferRequest? LastPosted { get; private set; }

        /// <summary>What a lookup by order number finds: a transfer an earlier attempt made.</summary>
        public InventoryTransfer? InSap { get; init; }
        public Exception? LookupFailure { get; init; }
        public List<string> LookedUp { get; } = [];

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.CreateInventoryTransferAsync) =>
                    Create((CreateInventoryTransferRequest)args![0]!),
                nameof(ISAPServiceLayerClient.FindStockTransferByOrderNumberAsync) =>
                    Find((string)args![0]!),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<InventoryTransfer?> Find(string orderNumber)
        {
            LookedUp.Add(orderNumber);
            return LookupFailure is null
                ? Task.FromResult(InSap)
                : Task.FromException<InventoryTransfer?>(LookupFailure);
        }

        private async Task<InventoryTransfer> Create(CreateInventoryTransferRequest request)
        {
            EnteredSap.TrySetResult();
            await ReleaseSap.Task;
            LastPosted = request;

            if (Failure is not null) throw Failure;

            TransfersCreated++;
            return new InventoryTransfer
            {
                DocEntry = 9001,
                DocNum = 9001,
                FromWarehouse = request.FromWarehouse,
                ToWarehouse = request.ToWarehouse
            };
        }
    }

    /// <summary>
    /// The store opens its own scope per call; this hands every one of them a context over the
    /// same in-memory database so the key is visible across posts.
    /// </summary>
    private sealed class SingleContextScopeFactory(DbContextOptions<ApplicationDbContext> options)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType)
            => serviceType == typeof(ApplicationDbContext) ? new ApplicationDbContext(options) : null;
        public void Dispose() { }
    }
}
