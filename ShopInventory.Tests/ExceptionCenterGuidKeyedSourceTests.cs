using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.ExceptionCenter;
using ShopInventory.Features.ExceptionCenter.Commands.AcknowledgeExceptionCenterItem;
using ShopInventory.Features.ExceptionCenter.Commands.AssignExceptionCenterItem;
using ShopInventory.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;
using ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;
using ShopInventory.Features.InventoryTransfers.Commands.RetryPendingRequestEditApply;
using ShopInventory.Features.InventoryTransfers.Commands.RetryPendingTransferPost;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the two classes of stuck work whose entities are Guid keyed — approval-gated transfers
/// that failed to post, and approved transfer request changes that failed to apply — and the
/// routing key that lets the exception center carry them alongside the int-keyed queues.
/// </summary>
/// <remarks>
/// These are the only failures with no queue row behind them: nothing retries them on a timer, so
/// if the exception center cannot list them nobody finds out the stock never moved. The key scheme
/// is the load-bearing part. Every int-keyed item has to keep answering to the value it always
/// did, or acknowledgements recorded before Guid sources existed detach from their item; and two
/// Guid items in one source must not collapse onto each other, which is exactly what happens if
/// they share the int ItemId of zero.
/// </remarks>
public sealed class ExceptionCenterGuidKeyedSourceTests : IDisposable
{
    private static readonly DateTime Created = new(2026, 7, 28, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Decided = new(2026, 7, 29, 9, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ExceptionCenterGuidKeyedSourceTests()
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

    // ── The key scheme ──────────────────────────────────

    [Fact]
    public void An_int_keyed_item_answers_to_the_id_it_always_did()
    {
        var items = new List<ExceptionCenterItemDto>
        {
            new() { Source = ExceptionCenterSources.InvoiceQueue, ItemId = 4172 }
        };

        GetExceptionCenterHandler.EnsureItemKeys(items);

        Assert.Equal("4172", items[0].ItemKey);
    }

    [Fact]
    public void A_guid_keyed_items_key_is_left_as_the_loader_set_it()
    {
        var id = Guid.NewGuid();
        var items = new List<ExceptionCenterItemDto>
        {
            new()
            {
                Source = ExceptionCenterSources.PendingInventoryTransferPost,
                ItemId = 0,
                ItemKey = ExceptionCenterSources.Key(id)
            }
        };

        GetExceptionCenterHandler.EnsureItemKeys(items);

        Assert.Equal(id.ToString("D"), items[0].ItemKey);
    }

    [Fact]
    public void A_state_written_against_an_int_id_still_finds_its_item()
    {
        // States predating the routing key identify their item by ItemId alone; the migration
        // backfills ItemKey from it, so this is the shape they arrive in afterwards.
        var items = new List<ExceptionCenterItemDto>
        {
            new() { Source = ExceptionCenterSources.InvoiceQueue, ItemId = 91 }
        };
        GetExceptionCenterHandler.EnsureItemKeys(items);

        GetExceptionCenterHandler.ApplyStates(items,
        [
            new ExceptionCenterItemStateEntity
            {
                Source = ExceptionCenterSources.InvoiceQueue,
                ItemId = 91,
                ItemKey = "91",
                IsAcknowledged = true,
                AcknowledgedByUsername = "tmoyo"
            }
        ]);

        Assert.True(items[0].IsAcknowledged);
        Assert.Equal("tmoyo", items[0].AcknowledgedByUsername);
    }

    [Fact]
    public void Acknowledging_one_guid_keyed_item_does_not_acknowledge_its_neighbour()
    {
        // Both carry ItemId zero, so on the old int key these two were the same item.
        var acknowledged = Guid.NewGuid();
        var untouched = Guid.NewGuid();

        var items = new List<ExceptionCenterItemDto>
        {
            GuidItem(ExceptionCenterSources.PendingInventoryTransferPost, acknowledged),
            GuidItem(ExceptionCenterSources.PendingInventoryTransferPost, untouched)
        };

        GetExceptionCenterHandler.ApplyStates(items,
        [
            new ExceptionCenterItemStateEntity
            {
                Source = ExceptionCenterSources.PendingInventoryTransferPost,
                ItemId = 0,
                ItemKey = ExceptionCenterSources.Key(acknowledged),
                IsAcknowledged = true,
                AssignedToUsername = "rchirwa"
            }
        ]);

        Assert.True(items[0].IsAcknowledged);
        Assert.Equal("rchirwa", items[0].AssignedToUsername);
        Assert.False(items[1].IsAcknowledged);
        Assert.Null(items[1].AssignedToUsername);
    }

    [Fact]
    public void A_state_from_another_source_is_not_applied()
    {
        var id = Guid.NewGuid();
        var items = new List<ExceptionCenterItemDto>
        {
            GuidItem(ExceptionCenterSources.PendingInventoryTransferPost, id)
        };

        GetExceptionCenterHandler.ApplyStates(items,
        [
            new ExceptionCenterItemStateEntity
            {
                Source = ExceptionCenterSources.PendingTransferRequestEditApply,
                ItemKey = ExceptionCenterSources.Key(id),
                IsAcknowledged = true
            }
        ]);

        Assert.False(items[0].IsAcknowledged);
    }

    [Fact]
    public async Task Two_guid_keyed_states_in_one_source_can_coexist()
    {
        // The uniqueness that protects the int sources cannot be (Source, ItemId) any more: every
        // Guid-keyed state holds zero there.
        _context.ExceptionCenterItemStates.Add(State(ExceptionCenterSources.PendingInventoryTransferPost, Guid.NewGuid()));
        _context.ExceptionCenterItemStates.Add(State(ExceptionCenterSources.PendingInventoryTransferPost, Guid.NewGuid()));

        var exception = await Record.ExceptionAsync(() => _context.SaveChangesAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task One_item_cannot_hold_two_states()
    {
        var id = Guid.NewGuid();
        _context.ExceptionCenterItemStates.Add(State(ExceptionCenterSources.PendingInventoryTransferPost, id));
        await _context.SaveChangesAsync();

        _context.ExceptionCenterItemStates.Add(State(ExceptionCenterSources.PendingInventoryTransferPost, id));

        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }

    // ── The source registry ─────────────────────────────

    [Fact]
    public void Every_source_the_dashboard_serves_is_described()
    {
        string[] sources =
        [
            ExceptionCenterSources.InvoiceQueue,
            ExceptionCenterSources.InventoryTransferQueue,
            ExceptionCenterSources.MobileOrderPostProcessing,
            ExceptionCenterSources.PaymentCallback,
            ExceptionCenterSources.PaymentCallbackRejection,
            ExceptionCenterSources.CreditNoteFiscalization,
            ExceptionCenterSources.PendingInventoryTransferPost,
            ExceptionCenterSources.PendingTransferRequestEditApply
        ];

        foreach (var source in sources)
        {
            Assert.True(ExceptionCenterSources.IsKnown(source), source);
            Assert.NotEqual("unrecognised source", ExceptionCenterSources.DescribeSource(source));
        }
    }

    [Fact]
    public void A_source_is_recognised_however_it_is_cased_or_padded()
    {
        Assert.True(ExceptionCenterSources.IsKnown("  Pending-Inventory-Transfer-Post "));
        Assert.True(ExceptionCenterSources.IsGuidKeyed("  Pending-Inventory-Transfer-Post "));
    }

    [Fact]
    public void Only_the_two_approval_gated_sources_are_guid_keyed()
    {
        Assert.True(ExceptionCenterSources.IsGuidKeyed(ExceptionCenterSources.PendingInventoryTransferPost));
        Assert.True(ExceptionCenterSources.IsGuidKeyed(ExceptionCenterSources.PendingTransferRequestEditApply));
        Assert.False(ExceptionCenterSources.IsGuidKeyed(ExceptionCenterSources.InvoiceQueue));
        Assert.False(ExceptionCenterSources.IsGuidKeyed(ExceptionCenterSources.PaymentCallback));
    }

    [Fact]
    public void Both_new_sources_can_be_retried_and_the_recorded_ones_cannot()
    {
        Assert.True(ExceptionCenterSources.SupportsRetry(ExceptionCenterSources.PendingInventoryTransferPost));
        Assert.True(ExceptionCenterSources.SupportsRetry(ExceptionCenterSources.PendingTransferRequestEditApply));
        Assert.False(ExceptionCenterSources.SupportsRetry(ExceptionCenterSources.PaymentCallback));
        Assert.False(ExceptionCenterSources.SupportsRetry(ExceptionCenterSources.CreditNoteFiscalization));
    }

    [Fact]
    public void A_guid_key_carries_no_int_id()
    {
        Assert.Equal(0, ExceptionCenterSources.ToLegacyItemId(Guid.NewGuid().ToString("D")));
        Assert.Equal(507, ExceptionCenterSources.ToLegacyItemId("507"));
    }

    // ── Listing the stuck work ──────────────────────────

    [Fact]
    public async Task A_transfer_that_failed_to_post_is_listed()
    {
        var pending = PendingTransfer(PendingInventoryTransferStatuses.PostFailed);
        pending.LastError = "Item ITEM-1 has insufficient stock in WH01";
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();

        var items = await GetExceptionCenterHandler.LoadPendingTransferPostFailuresAsync(_context, 40, default);

        var item = Assert.Single(items);
        Assert.Equal(ExceptionCenterSources.PendingInventoryTransferPost, item.Source);
        Assert.Equal(pending.Id.ToString("D"), item.ItemKey);
        Assert.Equal("SAP Posting", item.Category);
        Assert.Equal(pending.DraftNumber, item.Reference);
        Assert.Equal("Item ITEM-1 has insufficient stock in WH01", item.LastError);
        Assert.True(item.CanRetry);
        // Ranked with the other failures, and dated from when the approval completed.
        Assert.Equal("Failed", item.Status);
        Assert.Equal(Decided, item.OccurredAtUtc);
        Assert.Equal(Created, item.CreatedAtUtc);
    }

    [Fact]
    public async Task A_transfer_still_moving_through_approval_is_not_listed()
    {
        foreach (var status in new[]
                 {
                     PendingInventoryTransferStatuses.AwaitingApproval,
                     PendingInventoryTransferStatuses.Approved,
                     PendingInventoryTransferStatuses.Posted,
                     PendingInventoryTransferStatuses.Rejected,
                     PendingInventoryTransferStatuses.Cancelled
                 })
        {
            _context.PendingInventoryTransfers.Add(PendingTransfer(status));
        }

        await _context.SaveChangesAsync();

        Assert.Empty(await GetExceptionCenterHandler.LoadPendingTransferPostFailuresAsync(_context, 40, default));
    }

    [Fact]
    public async Task A_held_transfer_with_no_draft_number_is_still_referenceable()
    {
        var pending = PendingTransfer(PendingInventoryTransferStatuses.PostFailed);
        pending.DraftNumber = null;
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();

        var items = await GetExceptionCenterHandler.LoadPendingTransferPostFailuresAsync(_context, 40, default);

        Assert.Contains(pending.Id.ToString("D"), Assert.Single(items).Reference);
    }

    [Fact]
    public async Task An_approved_change_that_failed_to_apply_is_listed()
    {
        var edit = PendingEdit(PendingTransferRequestEditStatuses.ApplyFailed);
        edit.LastError = "Transfer request 8811 no longer exists in SAP.";
        _context.PendingTransferRequestEdits.Add(edit);
        await _context.SaveChangesAsync();

        var items = await GetExceptionCenterHandler.LoadPendingRequestEditApplyFailuresAsync(_context, 40, default);

        var item = Assert.Single(items);
        Assert.Equal(ExceptionCenterSources.PendingTransferRequestEditApply, item.Source);
        Assert.Equal(edit.Id.ToString("D"), item.ItemKey);
        Assert.Equal("SAP Posting", item.Category);
        Assert.Contains("2451", item.Reference);
        Assert.Contains("8811", item.SourceSystem);
        Assert.Equal("Transfer request 8811 no longer exists in SAP.", item.LastError);
        Assert.True(item.CanRetry);
        Assert.Equal("Failed", item.Status);
        Assert.Equal(Decided, item.OccurredAtUtc);
    }

    [Fact]
    public async Task A_change_that_reached_SAP_or_was_rejected_is_not_listed()
    {
        foreach (var status in new[]
                 {
                     PendingTransferRequestEditStatuses.AwaitingApproval,
                     PendingTransferRequestEditStatuses.Applied,
                     PendingTransferRequestEditStatuses.Rejected,
                     PendingTransferRequestEditStatuses.Cancelled
                 })
        {
            _context.PendingTransferRequestEdits.Add(PendingEdit(status));
        }

        await _context.SaveChangesAsync();

        Assert.Empty(await GetExceptionCenterHandler.LoadPendingRequestEditApplyFailuresAsync(_context, 40, default));
    }

    [Fact]
    public async Task Each_source_is_held_to_its_own_page_limit()
    {
        for (var index = 0; index < 5; index++)
        {
            _context.PendingInventoryTransfers.Add(PendingTransfer(PendingInventoryTransferStatuses.PostFailed));
        }

        await _context.SaveChangesAsync();

        Assert.Equal(2, (await GetExceptionCenterHandler.LoadPendingTransferPostFailuresAsync(_context, 2, default)).Count);
    }

    [Fact]
    public async Task The_most_recent_failure_is_listed_first()
    {
        var older = PendingTransfer(PendingInventoryTransferStatuses.PostFailed);
        older.DecidedAtUtc = Decided.AddDays(-3);
        var newer = PendingTransfer(PendingInventoryTransferStatuses.PostFailed);
        newer.DecidedAtUtc = Decided;

        _context.PendingInventoryTransfers.AddRange(older, newer);
        await _context.SaveChangesAsync();

        var items = await GetExceptionCenterHandler.LoadPendingTransferPostFailuresAsync(_context, 40, default);

        Assert.Equal(newer.Id.ToString("D"), items[0].ItemKey);
    }

    // ── Acknowledging and assigning ─────────────────────

    [Fact]
    public async Task A_failed_transfer_can_be_acknowledged_by_its_guid()
    {
        var pending = PendingTransfer(PendingInventoryTransferStatuses.PostFailed);
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();

        var result = await Acknowledge(
            ExceptionCenterSources.PendingInventoryTransferPost, pending.Id.ToString("D"));

        Assert.False(result.IsError);
        var state = Assert.Single(await _context.ExceptionCenterItemStates.ToListAsync());
        Assert.Equal(pending.Id.ToString("D"), state.ItemKey);
        Assert.True(state.IsAcknowledged);
        Assert.Equal("kmutasa", state.AcknowledgedByUsername);
        // Nothing to record in the legacy column for a Guid-keyed source.
        Assert.Equal(0, state.ItemId);
    }

    [Fact]
    public async Task Acknowledging_twice_updates_the_one_state()
    {
        var pending = PendingTransfer(PendingInventoryTransferStatuses.PostFailed);
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();

        await Acknowledge(ExceptionCenterSources.PendingInventoryTransferPost, pending.Id.ToString("D"));
        await Acknowledge(ExceptionCenterSources.PendingInventoryTransferPost, pending.Id.ToString("D"));

        Assert.Single(await _context.ExceptionCenterItemStates.ToListAsync());
    }

    [Fact]
    public async Task An_item_that_is_not_there_cannot_be_acknowledged()
    {
        var result = await Acknowledge(
            ExceptionCenterSources.PendingInventoryTransferPost, Guid.NewGuid().ToString("D"));

        Assert.True(result.IsError);
        Assert.Equal("ExceptionCenter.ItemNotFound", result.FirstError.Code);
    }

    [Fact]
    public async Task A_guid_keyed_source_will_not_accept_an_int_key()
    {
        var pending = PendingTransfer(PendingInventoryTransferStatuses.PostFailed);
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();

        var result = await Acknowledge(ExceptionCenterSources.PendingInventoryTransferPost, "0");

        Assert.True(result.IsError);
        Assert.Equal("ExceptionCenter.ItemNotFound", result.FirstError.Code);
    }

    [Fact]
    public async Task An_int_keyed_source_will_not_accept_a_guid_key()
    {
        var result = await Acknowledge(ExceptionCenterSources.InvoiceQueue, Guid.NewGuid().ToString("D"));

        Assert.True(result.IsError);
        Assert.Equal("ExceptionCenter.ItemNotFound", result.FirstError.Code);
    }

    [Fact]
    public async Task A_failed_change_can_be_assigned_by_its_guid()
    {
        var edit = PendingEdit(PendingTransferRequestEditStatuses.ApplyFailed);
        _context.PendingTransferRequestEdits.Add(edit);
        await _context.SaveChangesAsync();

        var handler = new AssignExceptionCenterItemHandler(
            _context, HttpContext(), NullLogger<AssignExceptionCenterItemHandler>.Instance);

        var result = await handler.Handle(
            new AssignExceptionCenterItemCommand(
                ExceptionCenterSources.PendingTransferRequestEditApply, edit.Id.ToString("D")),
            default);

        Assert.False(result.IsError);
        var state = Assert.Single(await _context.ExceptionCenterItemStates.ToListAsync());
        Assert.Equal(edit.Id.ToString("D"), state.ItemKey);
        Assert.Equal("kmutasa", state.AssignedToUsername);
    }

    // ── Retrying ────────────────────────────────────────

    [Fact]
    public async Task Retrying_a_failed_transfer_reposts_it_through_the_transfer_feature()
    {
        var pending = PendingTransfer(PendingInventoryTransferStatuses.PostFailed);
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();

        var mediator = new RecordingMediator();
        var result = await Retry(
            mediator, ExceptionCenterSources.PendingInventoryTransferPost, pending.Id.ToString("D"));

        Assert.False(result.IsError);
        // Reused rather than reimplemented, so the retry inherits the SAP, status and warehouse
        // scope guards the transfer feature already applies.
        var sent = Assert.IsType<RetryPendingTransferPostCommand>(Assert.Single(mediator.Sent));
        Assert.Equal(pending.Id, sent.PendingTransferId);
        Assert.Equal(CurrentUserId, sent.UserId);
    }

    [Fact]
    public async Task Retrying_a_failed_change_reapplies_it_through_the_transfer_feature()
    {
        var edit = PendingEdit(PendingTransferRequestEditStatuses.ApplyFailed);
        _context.PendingTransferRequestEdits.Add(edit);
        await _context.SaveChangesAsync();

        var mediator = new RecordingMediator();
        var result = await Retry(
            mediator, ExceptionCenterSources.PendingTransferRequestEditApply, edit.Id.ToString("D"));

        Assert.False(result.IsError);
        var sent = Assert.IsType<RetryPendingRequestEditApplyCommand>(Assert.Single(mediator.Sent));
        Assert.Equal(edit.Id, sent.PendingEditId);
        Assert.Equal(CurrentUserId, sent.UserId);
    }

    [Fact]
    public async Task A_refused_repost_is_reported_rather_than_swallowed()
    {
        var pending = PendingTransfer(PendingInventoryTransferStatuses.PostFailed);
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();

        var mediator = new RecordingMediator
        {
            Failure = Error.Conflict("InventoryTransfer.PendingTransferNotActionable", "This transfer is Posted.")
        };

        var result = await Retry(
            mediator, ExceptionCenterSources.PendingInventoryTransferPost, pending.Id.ToString("D"));

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.PendingTransferNotActionable", result.FirstError.Code);
    }

    [Fact]
    public async Task A_malformed_guid_key_is_not_dispatched()
    {
        var mediator = new RecordingMediator();

        var result = await Retry(mediator, ExceptionCenterSources.PendingInventoryTransferPost, "not-a-guid");

        Assert.True(result.IsError);
        Assert.Equal("ExceptionCenter.ItemNotFound", result.FirstError.Code);
        Assert.Empty(mediator.Sent);
    }

    [Fact]
    public async Task A_retry_with_no_signed_in_user_is_refused()
    {
        var pending = PendingTransfer(PendingInventoryTransferStatuses.PostFailed);
        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();

        var mediator = new RecordingMediator();
        var handler = new RetryExceptionCenterItemHandler(
            _context,
            StubProxy.Unused<IInvoiceQueueService>(),
            StubProxy.Unused<IInventoryTransferQueueService>(),
            UnusedVanSalesPostingService(),
            mediator,
            new HttpContextAccessor(),
            NullLogger<RetryExceptionCenterItemHandler>.Instance);

        var result = await handler.Handle(
            new RetryExceptionCenterItemCommand(
                ExceptionCenterSources.PendingInventoryTransferPost, pending.Id.ToString("D")),
            default);

        Assert.True(result.IsError);
        Assert.Equal("ExceptionCenter.RetryFailed", result.FirstError.Code);
        Assert.Empty(mediator.Sent);
    }

    [Fact]
    public async Task A_source_that_only_records_what_happened_cannot_be_retried()
    {
        var mediator = new RecordingMediator();

        var result = await Retry(mediator, ExceptionCenterSources.PaymentCallback, "17");

        Assert.True(result.IsError);
        Assert.Equal("ExceptionCenter.RetryNotSupported", result.FirstError.Code);
        Assert.Empty(mediator.Sent);
    }

    [Fact]
    public async Task An_unrecognised_source_is_not_found_rather_than_unsupported()
    {
        var result = await Retry(new RecordingMediator(), "warehouse-gremlins", "17");

        Assert.True(result.IsError);
        Assert.Equal("ExceptionCenter.ItemNotFound", result.FirstError.Code);
    }

    // ── Helpers ─────────────────────────────────────────

    private static readonly Guid CurrentUserId = Guid.Parse("6f1a5b70-0c8e-4f2a-9c1d-2e7b4a9d3f11");

    private static ExceptionCenterItemDto GuidItem(string source, Guid id) => new()
    {
        Source = source,
        ItemId = 0,
        ItemKey = ExceptionCenterSources.Key(id)
    };

    private static ExceptionCenterItemStateEntity State(string source, Guid id) => new()
    {
        Source = source,
        ItemId = 0,
        ItemKey = ExceptionCenterSources.Key(id),
        IsAcknowledged = true
    };

    private int _draftSequence;

    /// <summary>A held transfer. Draft numbers are unique, so each one takes the next.</summary>
    private PendingInventoryTransferEntity PendingTransfer(string status) => new()
    {
        DraftNumber = $"DT-2026-{++_draftSequence:00000}",
        FromWarehouse = "WH01",
        ToWarehouse = "WH02",
        PayloadJson = "{}",
        Status = status,
        CreatedByUserId = Guid.NewGuid(),
        CreatedByName = "Tarisai Moyo",
        CreatedByRole = ApplicationRoles.DepotController,
        CreatedAtUtc = Created,
        DecidedAtUtc = Decided,
        LineCount = 3,
        TotalQuantity = 18m
    };

    private static PendingTransferRequestEditEntity PendingEdit(string status) => new()
    {
        RequestDocEntry = 8811,
        RequestDocNum = 2451,
        FromWarehouse = "WH01",
        ToWarehouse = "WH02",
        ProposedLinesJson = "[]",
        OriginalLinesJson = "[]",
        Status = status,
        CreatedByUserId = Guid.NewGuid(),
        CreatedByName = "Rudo Chirwa",
        CreatedByRole = ApplicationRoles.DepotController,
        CreatedAtUtc = Created,
        DecidedAtUtc = Decided
    };

    private Task<ErrorOr<Success>> Acknowledge(string source, string itemKey)
    {
        var handler = new AcknowledgeExceptionCenterItemHandler(
            _context, HttpContext(), NullLogger<AcknowledgeExceptionCenterItemHandler>.Instance);

        return handler.Handle(new AcknowledgeExceptionCenterItemCommand(source, itemKey), default);
    }

    private Task<ErrorOr<Success>> Retry(IMediator mediator, string source, string itemKey)
    {
        var handler = new RetryExceptionCenterItemHandler(
            _context,
            StubProxy.Unused<IInvoiceQueueService>(),
            StubProxy.Unused<IInventoryTransferQueueService>(),
            UnusedVanSalesPostingService(),
            mediator,
            HttpContext(),
            NullLogger<RetryExceptionCenterItemHandler>.Instance);

        return handler.Handle(new RetryExceptionCenterItemCommand(source, itemKey), default);
    }

    /// <summary>
    /// A real service over the same context, since it is a class rather than an interface and cannot be
    /// stubbed. Nothing here retries a van sale, so its SAP client is never reached.
    /// </summary>
    private VanSalesEndOfDayPostingService UnusedVanSalesPostingService()
        => new(
            _context,
            StubProxy.Unused<ISAPServiceLayerClient>(),
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            Options.Create(new VanSalesPostingSettings()),
            NullLogger<VanSalesEndOfDayPostingService>.Instance);

    private static IHttpContextAccessor HttpContext()
    {
        var context = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "kmutasa"),
                    new System.Security.Claims.Claim(
                        System.Security.Claims.ClaimTypes.NameIdentifier, CurrentUserId.ToString())
                ],
                "Test"))
        };

        return new HttpContextAccessor { HttpContext = context };
    }

    /// <summary>Records what the exception center delegated, without running it.</summary>
    private sealed class RecordingMediator : IMediator
    {
        public List<object> Sent { get; } = [];
        public Error? Failure { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);

            // The exception center only reads IsError and Errors off these, so an empty success
            // payload is enough to stand in for the real response.
            var responseType = typeof(TResponse);
            var valueType = responseType.GetGenericArguments()[0];
            var value = Failure is null
                ? Activator.CreateInstance(valueType)!
                : null;

            var response = Failure is null
                ? responseType.GetMethod("op_Implicit", [valueType])!.Invoke(null, [value])!
                : responseType.GetMethod("op_Implicit", [typeof(Error)])!.Invoke(null, [Failure.Value])!;

            return Task.FromResult((TResponse)response);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => throw new NotSupportedException();
    }
}
