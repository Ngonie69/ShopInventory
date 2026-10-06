using ErrorOr;
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
using ShopInventory.Features.InventoryTransfers.Commands.EditPendingTransferLines;
using ShopInventory.Features.InventoryTransfers.Commands.PostPendingTransferLinesInStock;
using ShopInventory.Features.InventoryTransfers.Commands.RetryPendingTransferPost;
using ShopInventory.Features.InventoryTransfers.Commands.RecordPendingTransferSapDocument;
using ShopInventory.Features.InventoryTransfers.Commands.WithdrawPendingTransfer;
using ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransferStockCheck;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The three ways out for an approved transfer that failed to post, besides retrying it whole:
/// post the lines the depot can fill, withdraw it, or record the document SAP created anyway.
/// </summary>
/// <remarks>
/// Each test reads the outcome back through a fresh context, so it proves what was written rather
/// than what the handler's tracked copy says.
/// </remarks>
public sealed class PendingTransferRecoveryTests : IDisposable
{
    private static readonly Guid Controller = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;

    public PendingTransferRecoveryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── Posting the lines in stock ─────────────────────────────────────────────

    [Fact]
    public async Task Posting_the_lines_in_stock_sends_them_and_records_what_was_left_out()
    {
        var pending = await GivenFailedTransferAsync(("YOG100", 360), ("MLK201", 240), ("YOG017", 34), ("JCE110", 96));
        var stock = new DepotStock { ["YOG100"] = 0, ["YOG017"] = 10 };
        var sap = new RecordingSap();

        var result = await PostInStock(stock, sap).Handle(
            new PostPendingTransferLinesInStockCommand(pending.Id, Controller), default);

        Assert.False(result.IsError);
        Assert.Contains("2 of 4 lines", result.Value.Message);

        var sent = Assert.Single(sap.Created);
        Assert.Equal(["MLK201", "JCE110"], sent.Lines!.Select(line => line.ItemCode));

        var stored = await ReadAsync(pending.Id);
        Assert.Equal(PendingInventoryTransferStatuses.Posted, stored.Status);
        Assert.Equal(2, stored.LineCount);
        Assert.Equal(336, stored.TotalQuantity);
        Assert.Null(stored.LastError);

        var dropped = PendingTransferDroppedLines.Read(stored.DroppedLinesJson);
        Assert.Collection(dropped,
            line => { Assert.Equal("YOG100", line.ItemCode); Assert.Equal(360, line.RequestedQuantity); Assert.Equal(0, line.AvailableQuantity); },
            line => { Assert.Equal("YOG017", line.ItemCode); Assert.Equal(10, line.AvailableQuantity); });

        // The stored payload is what reached SAP, and keeps the requester's comments rather than the
        // remarks the poster composes for SAP on each attempt.
        var payload = PendingInventoryTransferMapper.DeserializePayload(stored);
        Assert.Equal(2, payload.Lines!.Count);
        Assert.Equal("Van restock", payload.Comments);
    }

    [Fact]
    public async Task Posting_the_lines_in_stock_when_all_are_in_stock_posts_it_whole()
    {
        var pending = await GivenFailedTransferAsync(("MLK201", 240), ("JCE110", 96));
        var sap = new RecordingSap();

        var result = await PostInStock(new DepotStock(), sap).Handle(
            new PostPendingTransferLinesInStockCommand(pending.Id, Controller), default);

        Assert.False(result.IsError);
        Assert.Equal(2, Assert.Single(sap.Created).Lines!.Count);
        Assert.Null((await ReadAsync(pending.Id)).DroppedLinesJson);
    }

    [Fact]
    public async Task Nothing_is_posted_when_no_line_is_in_stock()
    {
        var pending = await GivenFailedTransferAsync(("YOG100", 360), ("YOG126", 360));
        var sap = new RecordingSap();

        var result = await PostInStock(new DepotStock { ["YOG100"] = 0, ["YOG126"] = 0 }, sap).Handle(
            new PostPendingTransferLinesInStockCommand(pending.Id, Controller), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.NothingInStockToPost", result.FirstError.Code);
        Assert.Empty(sap.Created);

        var stored = await ReadAsync(pending.Id);
        Assert.Equal(PendingInventoryTransferStatuses.PostFailed, stored.Status);
        Assert.Equal(2, stored.LineCount);
    }

    [Fact]
    public async Task Nothing_is_posted_against_stock_that_could_not_be_read()
    {
        var pending = await GivenFailedTransferAsync(("YOG100", 360), ("MLK201", 240));
        var stock = new DepotStock { ["YOG100"] = 0 };
        stock.Unreadable = true;
        var sap = new RecordingSap();

        var result = await PostInStock(stock, sap).Handle(
            new PostPendingTransferLinesInStockCommand(pending.Id, Controller), default);

        Assert.True(result.IsError);
        Assert.Empty(sap.Created);
    }

    [Fact]
    public async Task A_timed_out_post_must_be_checked_in_SAP_before_part_of_it_is_posted()
    {
        var pending = await GivenFailedTransferAsync(("MLK201", 240));
        await SetLastErrorAsync(pending.Id,
            "The SAP post timed out before SAP answered, so it is not known whether the transfer was created. "
            + "Check SAP for this transfer before retrying — retrying will post it again.");
        var sap = new RecordingSap();

        var result = await PostInStock(new DepotStock(), sap).Handle(
            new PostPendingTransferLinesInStockCommand(pending.Id, Controller), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.PostOutcomeUnknown", result.FirstError.Code);
        Assert.Empty(sap.Created);
    }

    // ── Withdrawing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Withdrawing_a_failed_transfer_closes_it_and_keeps_its_approval_time()
    {
        var pending = await GivenFailedTransferAsync(("YOG100", 360));
        var decidedAt = pending.DecidedAtUtc;

        var result = await Withdraw(BuildStore()).Handle(
            new WithdrawPendingTransferCommand(pending.Id, Controller, "  Depot out of yoghurt until October  "), default);

        Assert.False(result.IsError);
        var stored = await ReadAsync(pending.Id);
        Assert.Equal(PendingInventoryTransferStatuses.Cancelled, stored.Status);
        Assert.Equal("Depot out of yoghurt until October", stored.WithdrawalReason);
        Assert.Equal(Controller, stored.WithdrawnByUserId);
        Assert.NotNull(stored.WithdrawnAtUtc);
        Assert.Equal(decidedAt, stored.DecidedAtUtc);
    }

    [Fact]
    public async Task A_transfer_that_is_posting_right_now_cannot_be_withdrawn()
    {
        var pending = await GivenFailedTransferAsync(("YOG100", 360));
        var store = BuildStore();

        // A retry in flight holds the poster's claim while the record still reads PostFailed.
        var held = await store.TryAcquireAsync<InventoryTransferDto>(
            PendingInventoryTransferPoster.IdempotencyScope, pending.Id.ToString(),
            new { PendingTransferId = pending.Id }, default);
        Assert.Equal(IdempotencyAcquireOutcome.Acquired, held.Outcome);

        var result = await Withdraw(store).Handle(
            new WithdrawPendingTransferCommand(pending.Id, Controller, "not needed"), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.PostInProgress", result.FirstError.Code);
        Assert.Equal(PendingInventoryTransferStatuses.PostFailed, (await ReadAsync(pending.Id)).Status);
    }

    [Fact]
    public async Task A_transfer_still_awaiting_approval_is_not_withdrawn_here()
    {
        var pending = await GivenFailedTransferAsync(("YOG100", 360));
        await SetStatusAsync(pending.Id, PendingInventoryTransferStatuses.AwaitingApproval);

        var result = await Withdraw(BuildStore()).Handle(
            new WithdrawPendingTransferCommand(pending.Id, Controller, "not needed"), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.WithdrawalNotAllowed", result.FirstError.Code);
    }

    [Fact]
    public async Task A_withdrawal_releases_the_claim_so_nothing_is_left_locked()
    {
        var pending = await GivenFailedTransferAsync(("YOG100", 360));
        var store = BuildStore();

        await Withdraw(store).Handle(new WithdrawPendingTransferCommand(pending.Id, Controller, "not needed"), default);

        var after = await store.TryAcquireAsync<InventoryTransferDto>(
            PendingInventoryTransferPoster.IdempotencyScope, pending.Id.ToString(),
            new { PendingTransferId = pending.Id }, default);
        Assert.Equal(IdempotencyAcquireOutcome.Acquired, after.Outcome);
    }

    // ── Editing the lines before posting ───────────────────────────────────────

    [Fact]
    public async Task Lowering_a_short_line_to_what_the_depot_has_lets_the_whole_transfer_post()
    {
        // DT-2026-00329: YOG144 asked 720 against 715 on hand, and SAP refused all five lines.
        var pending = await GivenFailedTransferAsync(("LAC005", 10), ("YOG144", 720), ("RMA001", 4275));
        var stock = new DepotStock { ["YOG144"] = 715 };
        var sap = new RecordingSap();

        var edited = await EditLines(BuildStore()).Handle(
            new EditPendingTransferLinesCommand(pending.Id, Controller, [new() { LineNum = 1, Quantity = 715 }], "5 short at the depot"),
            default);

        Assert.False(edited.IsError);
        Assert.Contains("YOG144 720 → 715", edited.Value.Message);

        var stored = await ReadAsync(pending.Id);
        Assert.Equal(PendingInventoryTransferStatuses.Approved, stored.Status);
        Assert.Null(stored.LastError);
        Assert.Equal(3, stored.LineCount);
        Assert.Equal(10 + 715 + 4275, stored.TotalQuantity);
        Assert.Equal(pending.DecidedAtUtc, stored.DecidedAtUtc);

        var posted = await Retry(stock, sap).Handle(new RetryPendingTransferPostCommand(pending.Id, Controller), default);

        Assert.False(posted.IsError);
        var sent = Assert.Single(sap.Created);
        Assert.Equal([10m, 715m, 4275m], sent.Lines!.Select(line => line.Quantity));
        Assert.Equal(PendingInventoryTransferStatuses.Posted, (await ReadAsync(pending.Id)).Status);
    }

    [Fact]
    public async Task A_quantity_above_the_approved_one_is_refused_and_nothing_changes()
    {
        var pending = await GivenFailedTransferAsync(("YOG144", 720), ("RMA001", 4275));

        var result = await EditLines(BuildStore()).Handle(
            new EditPendingTransferLinesCommand(pending.Id, Controller,
                [new() { LineNum = 0, Quantity = 700 }, new() { LineNum = 1, Quantity = 5000 }], null),
            default);

        Assert.True(result.IsError);
        Assert.Contains("more than the 4275 approved", result.FirstError.Description);

        // All or nothing: the valid half of the edit is not kept either.
        var stored = await ReadAsync(pending.Id);
        Assert.Equal(PendingInventoryTransferStatuses.PostFailed, stored.Status);
        Assert.Equal([720m, 4275m], PendingInventoryTransferMapper.DeserializePayload(stored).Lines!.Select(line => line.Quantity));
    }

    [Fact]
    public async Task A_zero_takes_the_line_out_but_not_every_line()
    {
        var pending = await GivenFailedTransferAsync(("YOG144", 720), ("RMA001", 4275));

        var all = await EditLines(BuildStore()).Handle(
            new EditPendingTransferLinesCommand(pending.Id, Controller,
                [new() { LineNum = 0, Quantity = 0 }, new() { LineNum = 1, Quantity = 0 }], null),
            default);
        Assert.True(all.IsError);
        Assert.Contains("Withdraw the transfer instead", all.FirstError.Description);

        var one = await EditLines(BuildStore()).Handle(
            new EditPendingTransferLinesCommand(pending.Id, Controller, [new() { LineNum = 0, Quantity = 0 }], null),
            default);
        Assert.False(one.IsError);

        var stored = await ReadAsync(pending.Id);
        var payload = PendingInventoryTransferMapper.DeserializePayload(stored);
        Assert.Equal("RMA001", Assert.Single(payload.Lines!).ItemCode);
        Assert.Equal("Van restock", payload.Comments);
        Assert.Equal(1, stored.LineCount);
    }

    [Fact]
    public async Task A_fractional_quantity_is_refused_for_a_unit_counted_whole()
    {
        var pending = await GivenFailedTransferAsync(("YOG144", 720));

        var result = await EditLines(BuildStore()).Handle(
            new EditPendingTransferLinesCommand(pending.Id, Controller, [new() { LineNum = 0, Quantity = 714.5m }], null),
            default);

        Assert.True(result.IsError);
        Assert.Contains("Fractional quantities are only allowed for KG items", result.FirstError.Description);
    }

    [Theory]
    [InlineData(PendingInventoryTransferStatuses.AwaitingApproval)]
    [InlineData(PendingInventoryTransferStatuses.Posted)]
    [InlineData(PendingInventoryTransferStatuses.Cancelled)]
    [InlineData(PendingInventoryTransferStatuses.Rejected)]
    public async Task Only_an_approved_unposted_transfer_can_be_edited(string status)
    {
        var pending = await GivenFailedTransferAsync(("YOG144", 720));
        await SetStatusAsync(pending.Id, status);

        var result = await EditLines(BuildStore()).Handle(
            new EditPendingTransferLinesCommand(pending.Id, Controller, [new() { LineNum = 0, Quantity = 715 }], null),
            default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.LineEditNotAllowed", result.FirstError.Code);
    }

    [Fact]
    public async Task A_transfer_that_is_posting_right_now_cannot_be_edited()
    {
        var pending = await GivenFailedTransferAsync(("YOG144", 720));
        var store = BuildStore();
        var held = await store.TryAcquireAsync<InventoryTransferDto>(
            PendingInventoryTransferPoster.IdempotencyScope, pending.Id.ToString(),
            new { PendingTransferId = pending.Id }, default);
        Assert.Equal(IdempotencyAcquireOutcome.Acquired, held.Outcome);

        var result = await EditLines(store).Handle(
            new EditPendingTransferLinesCommand(pending.Id, Controller, [new() { LineNum = 0, Quantity = 715 }], null),
            default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.PostInProgress", result.FirstError.Code);
        Assert.Equal(720, (await ReadAsync(pending.Id)).TotalQuantity);
    }

    [Fact]
    public async Task A_timed_out_post_must_be_checked_in_SAP_before_its_lines_change()
    {
        var pending = await GivenFailedTransferAsync(("YOG144", 720));
        await SetLastErrorAsync(pending.Id,
            "The SAP post timed out before SAP answered, so it is not known whether the transfer was created. "
            + "Check SAP for this transfer before retrying — retrying will post it again.");

        var result = await EditLines(BuildStore()).Handle(
            new EditPendingTransferLinesCommand(pending.Id, Controller, [new() { LineNum = 0, Quantity = 715 }], null),
            default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.PostOutcomeUnknown", result.FirstError.Code);
    }

    [Fact]
    public async Task An_edit_releases_the_claim_so_the_post_can_run()
    {
        var pending = await GivenFailedTransferAsync(("YOG144", 720));
        var store = BuildStore();

        await EditLines(store).Handle(
            new EditPendingTransferLinesCommand(pending.Id, Controller, [new() { LineNum = 0, Quantity = 715 }], null),
            default);

        var after = await store.TryAcquireAsync<InventoryTransferDto>(
            PendingInventoryTransferPoster.IdempotencyScope, pending.Id.ToString(),
            new { PendingTransferId = pending.Id }, default);
        Assert.Equal(IdempotencyAcquireOutcome.Acquired, after.Outcome);
    }

    [Fact]
    public void A_lowered_line_keeps_its_first_chosen_batches_until_they_cover_it()
    {
        var payload = new CreateInventoryTransferRequest
        {
            Lines =
            [
                new CreateInventoryTransferLineRequest
                {
                    ItemCode = "YOG144",
                    Quantity = 720,
                    UoMCode = "EA",
                    BatchNumbers =
                    [
                        new TransferBatchRequest { BatchNumber = "B-0901", Quantity = 400 },
                        new TransferBatchRequest { BatchNumber = "B-0915", Quantity = 300 },
                        new TransferBatchRequest { BatchNumber = "B-0930", Quantity = 20 }
                    ]
                }
            ]
        };

        var result = EditPendingTransferLinesHandler.ApplyEdits(payload, [new() { LineNum = 0, Quantity = 500 }]);

        Assert.False(result.IsError);
        var line = Assert.Single(result.Value.Lines);
        Assert.Collection(line.BatchNumbers!,
            batch => { Assert.Equal("B-0901", batch.BatchNumber); Assert.Equal(400, batch.Quantity); },
            batch => { Assert.Equal("B-0915", batch.BatchNumber); Assert.Equal(100, batch.Quantity); });
        Assert.Equal(720, payload.Lines[0].Quantity);
    }

    // ── Recording a document found in SAP ──────────────────────────────────────

    [Fact]
    public async Task Recording_the_SAP_document_closes_the_transfer_at_the_attempt_that_created_it()
    {
        var pending = await GivenFailedTransferAsync(("MLK201", 240));
        var attempted = DateTime.UtcNow.AddDays(-4);
        await SetLastAttemptAsync(pending.Id, attempted);
        var store = BuildStore();
        var sap = new RecordingSap
        {
            Existing = [new InventoryTransfer { DocEntry = 7001, DocNum = 51234, FromWarehouse = "KEFBYC", ToWarehouse = "VAN010" }]
        };

        var result = await Record(sap, store).Handle(
            new RecordPendingTransferSapDocumentCommand(pending.Id, Controller, 51234), default);

        Assert.False(result.IsError);
        var stored = await ReadAsync(pending.Id);
        Assert.Equal(PendingInventoryTransferStatuses.Posted, stored.Status);
        Assert.Equal(51234, stored.SapDocNum);
        Assert.Equal(7001, stored.SapDocEntry);
        Assert.True(stored.PostRecordedManually);
        Assert.Equal(attempted, stored.PostedAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Empty(sap.Created);

        // A later post must replay this document, not create a second one.
        var replay = await store.TryAcquireAsync<InventoryTransferDto>(
            PendingInventoryTransferPoster.IdempotencyScope, pending.Id.ToString(),
            new { PendingTransferId = pending.Id }, default);
        Assert.Equal(IdempotencyAcquireOutcome.ReplayAvailable, replay.Outcome);
        Assert.Equal(51234, replay.Response!.DocNum);
    }

    [Fact]
    public async Task A_number_SAP_does_not_have_for_this_van_is_refused()
    {
        var pending = await GivenFailedTransferAsync(("MLK201", 240));
        var sap = new RecordingSap
        {
            Existing = [new InventoryTransfer { DocEntry = 7001, DocNum = 51234, FromWarehouse = "KEFBYC", ToWarehouse = "VAN010" }]
        };

        var result = await Record(sap, BuildStore()).Handle(
            new RecordPendingTransferSapDocumentCommand(pending.Id, Controller, 99999), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.SapTransferNotFound", result.FirstError.Code);
        Assert.Equal(PendingInventoryTransferStatuses.PostFailed, (await ReadAsync(pending.Id)).Status);
    }

    [Fact]
    public async Task A_document_from_another_depot_is_not_this_request()
    {
        var pending = await GivenFailedTransferAsync(("MLK201", 240));
        var sap = new RecordingSap
        {
            Existing = [new InventoryTransfer { DocEntry = 7001, DocNum = 51234, FromWarehouse = "KEFGRC", ToWarehouse = "VAN010" }]
        };

        var result = await Record(sap, BuildStore()).Handle(
            new RecordPendingTransferSapDocumentCommand(pending.Id, Controller, 51234), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.SapTransferDoesNotMatch", result.FirstError.Code);
    }

    [Fact]
    public async Task A_number_already_recorded_against_another_request_is_refused()
    {
        var first = await GivenFailedTransferAsync(("MLK201", 240));
        var second = await GivenFailedTransferAsync(("MLK201", 240));
        var sap = new RecordingSap
        {
            Existing = [new InventoryTransfer { DocEntry = 7001, DocNum = 51234, FromWarehouse = "KEFBYC", ToWarehouse = "VAN010" }]
        };

        Assert.False((await Record(sap, BuildStore()).Handle(
            new RecordPendingTransferSapDocumentCommand(first.Id, Controller, 51234), default)).IsError);

        var result = await Record(sap, BuildStore()).Handle(
            new RecordPendingTransferSapDocumentCommand(second.Id, Controller, 51234), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.SapTransferAlreadyRecorded", result.FirstError.Code);
    }

    // ── The stock check ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_stock_check_puts_short_lines_first_with_what_the_depot_has()
    {
        var pending = await GivenFailedTransferAsync(("MLK201", 240), ("YOG100", 360), ("JCE110", 96));

        var result = await new GetPendingTransferStockCheckHandler(
                NewContext(), Authorizer(), new DepotStock { ["YOG100"] = 40 }.AsService())
            .Handle(new GetPendingTransferStockCheckQuery(pending.Id, Controller), default);

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.LinesInStock);
        Assert.Equal(1, result.Value.LinesShort);
        var first = result.Value.Lines[0];
        Assert.Equal("YOG100", first.ItemCode);
        Assert.Equal(PendingTransferStockLineStates.Short, first.State);
        Assert.Equal(40, first.AvailableQuantity);
        Assert.All(result.Value.Lines.Skip(1), line => Assert.Null(line.AvailableQuantity));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────

    private async Task<PendingInventoryTransferEntity> GivenFailedTransferAsync(params (string Item, decimal Quantity)[] lines)
    {
        var pending = new PendingInventoryTransferEntity
        {
            Id = Guid.NewGuid(),
            DraftNumber = $"DT-2026-{Random.Shared.Next(10000, 99999)}",
            FromWarehouse = "KEFBYC",
            ToWarehouse = "VAN010",
            Status = PendingInventoryTransferStatuses.PostFailed,
            CreatedByUserId = Controller,
            CreatedByName = "Bulawayo Controller",
            CreatedByRole = ApplicationRoles.DepotController,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-5),
            DecidedAtUtc = DateTime.UtcNow.AddDays(-5).AddMinutes(7),
            LineCount = lines.Length,
            TotalQuantity = lines.Sum(line => line.Quantity),
            LastError = "Insufficient stock in source warehouse: Insufficient stock for item 'YOG100' in warehouse 'KEFBYC'. Requested: 360, Available: 0, Shortage: 360",
            PayloadJson = PendingInventoryTransferMapper.SerializePayload(new CreateInventoryTransferRequest
            {
                FromWarehouse = "KEFBYC",
                ToWarehouse = "VAN010",
                Comments = "Van restock",
                Lines = lines.Select(line => new CreateInventoryTransferLineRequest
                {
                    ItemCode = line.Item,
                    Quantity = line.Quantity,
                    UoMCode = "EA",
                    FromWarehouseCode = "KEFBYC",
                    ToWarehouseCode = "VAN010"
                }).ToList()
            })
        };

        _context.PendingInventoryTransfers.Add(pending);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return pending;
    }

    private async Task SetLastErrorAsync(Guid id, string message)
        => await _context.PendingInventoryTransfers.Where(item => item.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.LastError, message));

    private async Task SetStatusAsync(Guid id, string status)
        => await _context.PendingInventoryTransfers.Where(item => item.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, status));

    private async Task SetLastAttemptAsync(Guid id, DateTime attemptedUtc)
        => await _context.PendingInventoryTransfers.Where(item => item.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.LastAttemptedAtUtc, attemptedUtc));

    private async Task<PendingInventoryTransferEntity> ReadAsync(Guid id)
    {
        await using var verify = NewContext();
        return await verify.PendingInventoryTransfers.AsNoTracking().FirstAsync(item => item.Id == id);
    }

    private ApplicationDbContext NewContext() => new(_options);

    private IIdempotencyRequestStore BuildStore()
        => new IdempotencyRequestStore(new ScopeFactory(_options), Options.Create(new SecuritySettings()));

    private static IOptions<SAPSettings> SapOn => Options.Create(new SAPSettings { Enabled = true });

    private static ITransferWarehouseAuthorizer Authorizer()
        => StubProxy.For<ITransferWarehouseAuthorizer>((_, _) => Task.FromResult<ErrorOr<Success>>(Result.Success));

    private static IAuditService Audit() => StubProxy.For<IAuditService>((_, _) => Task.CompletedTask);

    private static IInventoryTransferApprovalService Approvals()
        => StubProxy.For<IInventoryTransferApprovalService>((method, _) => method.Name switch
        {
            nameof(IInventoryTransferApprovalService.GetProgressAsync) =>
                throw new InvalidOperationException("no approval request in this fixture"),
            nameof(IInventoryTransferApprovalService.MarkGeneratedAsync) => (object)Task.CompletedTask,
            _ => throw new InvalidOperationException($"Unexpected approval call: {method.Name}")
        });

    private PostPendingTransferLinesInStockHandler PostInStock(DepotStock stock, RecordingSap sap)
    {
        var context = NewContext();
        var poster = new PendingInventoryTransferPoster(
            context, sap.AsClient(), stock.AsService(), Approvals(), new NoOpNotificationService(), Audit(),
            BuildStore(), NullLogger<PendingInventoryTransferPoster>.Instance);
        return new PostPendingTransferLinesInStockHandler(context, Authorizer(), poster, Audit(), SapOn);
    }

    private RetryPendingTransferPostHandler Retry(DepotStock stock, RecordingSap sap)
    {
        var context = NewContext();
        var poster = new PendingInventoryTransferPoster(
            context, sap.AsClient(), stock.AsService(), Approvals(), new NoOpNotificationService(), Audit(),
            BuildStore(), NullLogger<PendingInventoryTransferPoster>.Instance);
        return new RetryPendingTransferPostHandler(context, Authorizer(), poster, SapOn);
    }

    private EditPendingTransferLinesHandler EditLines(IIdempotencyRequestStore store)
        => new(NewContext(), Authorizer(), store, Audit(), NullLogger<EditPendingTransferLinesHandler>.Instance);

    private WithdrawPendingTransferHandler Withdraw(IIdempotencyRequestStore store)
        => new(NewContext(), Authorizer(), store, Audit(), NullLogger<WithdrawPendingTransferHandler>.Instance);

    private RecordPendingTransferSapDocumentHandler Record(RecordingSap sap, IIdempotencyRequestStore store)
        => new(NewContext(), Authorizer(), sap.AsClient(), Approvals(), store, Audit(), SapOn,
            NullLogger<RecordPendingTransferSapDocumentHandler>.Instance);

    /// <summary>The depot's stock by item; an item not named has plenty.</summary>
    private sealed class DepotStock : Dictionary<string, decimal>
    {
        public bool Unreadable { get; set; }

        public IStockValidationService AsService() =>
            StubProxy.For<IStockValidationService>((method, args) => method.Name switch
            {
                nameof(IStockValidationService.ValidateInventoryTransferStockAsync) =>
                    (object)Task.FromResult(Validate((CreateInventoryTransferRequest)args![0]!)),
                _ => throw new InvalidOperationException($"Unexpected stock-validation call: {method.Name}")
            });

        private StockValidationResult Validate(CreateInventoryTransferRequest request)
        {
            var result = new StockValidationResult();
            if (Unreadable)
            {
                result.UnreadableWarehouses.Add("KEFBYC");
                return result;
            }

            for (var index = 0; index < request.Lines!.Count; index++)
            {
                var line = request.Lines[index];
                if (TryGetValue(line.ItemCode!, out var available) && available < line.Quantity)
                {
                    result.Errors.Add(new StockValidationError
                    {
                        LineNumber = index + 1,
                        ItemCode = line.ItemCode,
                        WarehouseCode = "KEFBYC",
                        RequestedQuantity = line.Quantity,
                        AvailableQuantity = available
                    });
                }
            }

            return result;
        }
    }

    private sealed class RecordingSap
    {
        public List<CreateInventoryTransferRequest> Created { get; } = [];
        public List<InventoryTransfer> Existing { get; init; } = [];

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.CreateInventoryTransferAsync) =>
                    (object)Task.FromResult(Create((CreateInventoryTransferRequest)args![0]!)),
                nameof(ISAPServiceLayerClient.GetInventoryTransfersByDateRangeAsync) =>
                    Task.FromResult(Existing),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private InventoryTransfer Create(CreateInventoryTransferRequest request)
        {
            Created.Add(request);
            return new InventoryTransfer
            {
                DocEntry = 9000 + Created.Count,
                DocNum = 9000 + Created.Count,
                FromWarehouse = request.FromWarehouse,
                ToWarehouse = request.ToWarehouse
            };
        }
    }

    private sealed class ScopeFactory(DbContextOptions<ApplicationDbContext> options)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType)
            => serviceType == typeof(ApplicationDbContext) ? new ApplicationDbContext(options) : null;
        public void Dispose() { }
    }
}
