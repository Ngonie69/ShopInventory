using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;
using ShopInventory.Features.SapTransferApprovals.Commands.AddApprovedSapTransfer;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Adding an inventory transfer that was keyed in the SAP client and approved there: the one SAP write,
/// the checks that come before it, and what is settled after it.
/// </summary>
/// <remarks>
/// The add moves stock out of a warehouse and happens once. Who may make it is who may convert a
/// transfer request; a batch-managed line without a pick is refused here with which line; a lost answer
/// is settled by the draft, which SAP closes when it converts; and a transfer request the draft was
/// copied from is closed so it cannot be converted again.
/// </remarks>
public sealed class SapTransferApprovalAddTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly IIdempotencyRequestStore _store;

    public SapTransferApprovalAddTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();
        _store = new IdempotencyRequestStore(new SingleDbContextScopeFactory(_options), Options.Create(new SecuritySettings()));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Adding_saves_the_draft_on_an_uncancellable_token_and_names_the_transfer_it_became()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(79744, "CMFC", "DISPYOG");
        var request = HeldTransfers.Request(74045, 79744);
        var sap = new FakeTransferSap(draft, request) { RequestsAfterSave = [request.Generated(151600)] };
        sap.Transfers[151600] = HeldTransfers.TransferLike(draft, 151600, 112400);
        var audit = new RecordingAuditService();

        var result = await Handler(sap, audit: audit).Handle(Command(79744, admin), CancellationToken.None);

        Assert.False(result.IsError, Describe(result));
        var save = Assert.Single(sap.Saves);
        Assert.Equal(79744, save.DraftEntry);
        Assert.False(save.Token.CanBeCanceled, "the save must run on a token the caller cannot cancel");

        Assert.True(result.Value.Resolved);
        Assert.Equal(151600, result.Value.TransferDocEntry);
        Assert.Equal(112400, result.Value.TransferDocNum);
        Assert.Equal("CMFC", result.Value.FromWarehouse);
        Assert.Contains("#112400", result.Value.Message);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AddApprovedSapTransfer, entry.Action);
        Assert.True(entry.Success);
    }

    /// <summary>
    /// SAP may answer the add with nothing and keep no request naming the transfer — the credit memo add
    /// deletes its request outright. The transfer is then the one posted since the add began, between
    /// the same warehouses, carrying exactly the draft's lines. Never a DocNum match.
    /// </summary>
    [Fact]
    public async Task When_sap_names_nothing_the_transfer_is_recognised_by_its_lines()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(
            80083, "PROYG", "EXPHW",
            HeldTransfers.Line(0, "YOG123", 2160m, "PROYG", "EXPHW", ("YOG123#L14", 2160m)),
            HeldTransfers.Line(1, "YOG124", 1200m, "PROYG", "EXPHW", ("YOG124#L20", 1200m)));
        var other = HeldTransfers.TransferLike(draft, 1001, 5001);
        other.StockTransferLines![1].Quantity = 1199m;
        var ours = HeldTransfers.TransferLike(draft, 1002, 5002);
        var sap = new FakeTransferSap(draft, HeldTransfers.Request(74352, 80083))
        {
            RequestsAfterSave = [],
            NewestBefore = 1000,
            TransfersAfter = [other, ours]
        };
        sap.Transfers[1002] = ours;

        var result = await Handler(sap).Handle(Command(80083, admin), CancellationToken.None);

        Assert.False(result.IsError, Describe(result));
        Assert.True(result.Value.Resolved);
        Assert.Equal(1002, result.Value.TransferDocEntry);
        Assert.Equal(5002, result.Value.TransferDocNum);
    }

    [Fact]
    public async Task Two_identical_transfers_since_the_add_are_not_guessed_between()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(80083, "PROYG", "EXPHW");
        var sap = new FakeTransferSap(draft, HeldTransfers.Request(74352, 80083))
        {
            RequestsAfterSave = [],
            TransfersAfter = [HeldTransfers.TransferLike(draft, 1001, 5001), HeldTransfers.TransferLike(draft, 1002, 5002)]
        };

        var result = await Handler(sap).Handle(Command(80083, admin), CancellationToken.None);

        Assert.False(result.IsError, "the add happened; only the identification is uncertain");
        Assert.False(result.Value.Resolved);
        Assert.Null(result.Value.TransferDocEntry);
        Assert.Contains("did not say which transfer", result.Value.Message);
    }

    /// <summary>
    /// SAP refuses the whole transfer for one batch-managed line without a complete pick. The add says
    /// which line before anything reaches SAP, rather than failing opaquely.
    /// </summary>
    [Fact]
    public async Task A_batch_managed_line_without_a_pick_is_refused_before_sap_with_the_line_named()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(
            61875, "CMB", "CMFS",
            HeldTransfers.Line(0, "FET017", 112m, "CMB", "CMFS"),
            HeldTransfers.Line(1, "CHE030", 50m, "CMB", "CMFS", ("CHE030/L21", 20m)),
            HeldTransfers.Line(2, "CRATE01", 4m, "CMB", "CMFS"));
        var sap = new FakeTransferSap(draft, HeldTransfers.Request(56603, 61875))
        {
            Management = HeldTransfers.BatchManaged("FET017", "CHE030")
        };

        var result = await Handler(sap).Handle(Command(61875, admin), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("SapTransferApproval.SelectionMissing", result.FirstError.Code);
        Assert.Contains("FET017 (112) has no batch picked", result.FirstError.Description);
        Assert.Contains("CHE030 (50) has batches picked for 20 of 50", result.FirstError.Description);
        Assert.DoesNotContain("CRATE01", result.FirstError.Description);
        Assert.Empty(sap.Saves);
    }

    /// <summary>When the item flags cannot be read, SAP decides — and its refusal is still said in words a person can act on.</summary>
    [Fact]
    public async Task Saps_own_refusal_for_a_missing_pick_says_what_to_do()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var sap = new FakeTransferSap(HeldTransfers.Draft(61875, "CMB", "CMFS"), HeldTransfers.Request(56603, 61875))
        {
            Management = null,
            RefuseWith = "Cannot add row without complete selection of batch/serial numbers"
        };
        var audit = new RecordingAuditService();

        var result = await Handler(sap, audit: audit).Handle(Command(61875, admin), CancellationToken.None);

        Assert.Equal("SapTransferApproval.SelectionMissing", result.FirstError.Code);
        Assert.Contains("Pick the batches on the draft in SAP Business One", result.FirstError.Description);
        Assert.Contains("Nothing was transferred", result.FirstError.Description);
        Assert.False(Assert.Single(audit.Entries).Success);
    }

    [Fact]
    public async Task Any_other_sap_refusal_is_passed_on_in_saps_words()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var sap = new FakeTransferSap(HeldTransfers.Draft(79744, "CMFC", "DISPYOG"), HeldTransfers.Request(74045, 79744))
        {
            RefuseWith = "Quantity falls into negative inventory  [StockTransferLines.ItemCode][line: 1]"
        };

        var result = await Handler(sap).Handle(Command(79744, admin), CancellationToken.None);

        Assert.Equal("SapTransferApproval.SapRejected", result.FirstError.Code);
        Assert.Contains("negative inventory", result.FirstError.Description);
    }

    [Fact]
    public async Task Only_an_approved_open_draft_with_no_unsettled_request_is_added()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);

        var pendingDraft = HeldTransfers.Draft(76917, "CMFS", "DISPYOG");
        pendingDraft.AuthorizationStatus = SapStockTransferAuthorizationStatuses.Pending;
        var pending = new FakeTransferSap(pendingDraft, HeldTransfers.Request(71285, 76917));
        var pendingResult = await Handler(pending).Handle(Command(76917, admin), CancellationToken.None);
        Assert.Equal("SapTransferApproval.NotApproved", pendingResult.FirstError.Code);
        Assert.Contains("Pending", pendingResult.FirstError.Description);

        var twoTemplates = new FakeTransferSap(
            HeldTransfers.Draft(76897, "CMFS", "RETURNS"),
            HeldTransfers.Request(71264, 76897),
            HeldTransfers.Request(71263, 76897, SapApprovalRequestStatuses.Pending));
        var twoResult = await Handler(twoTemplates).Handle(Command(76897, admin), CancellationToken.None);
        Assert.Equal("SapTransferApproval.ApprovalNotSettled", twoResult.FirstError.Code);
        Assert.Contains("71263", twoResult.FirstError.Description);

        var notATransfer = HeldTransfers.Draft(80163, "CMFC", "CMFS");
        notATransfer.DocObjectCode = "20";
        var goodsReceipt = new FakeTransferSap(notATransfer);
        Assert.Equal("SapTransferApproval.NotFound",
            (await Handler(goodsReceipt).Handle(Command(80163, admin), CancellationToken.None)).FirstError.Code);

        Assert.Empty(pending.Saves);
        Assert.Empty(twoTemplates.Saves);
    }

    /// <summary>
    /// A closed draft has been added already — by an earlier add here or in the B1 client. The refusal
    /// names the transfer and warns about a request it was copied from that is still open.
    /// </summary>
    [Fact]
    public async Task A_closed_draft_is_already_added_and_says_so()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = FakeTransferSap.Closed(HeldTransfers.Draft(
            78464, "CMFC", "CMFS", HeldTransfers.Line(2, "GOU016", 40m, "CMFC", "CMFS", ("GOU016/J18", 40m)).CopiedFrom(22788)));
        var sap = new FakeTransferSap(draft, HeldTransfers.Request(72754, 78464).Generated(151523));
        sap.Transfers[151523] = HeldTransfers.TransferLike(draft, 151523, 112376);
        sap.TransferRequests[22788] = HeldTransfers.OpenRequest(22788, 10688, "CMFC", "CMFS");

        var result = await Handler(sap).Handle(Command(78464, admin), CancellationToken.None);

        Assert.Equal("SapTransferApproval.AlreadyAdded", result.FirstError.Code);
        Assert.Contains("#112376", result.FirstError.Description);
        Assert.Contains("Transfer request #10688", result.FirstError.Description);
        Assert.Empty(sap.Saves);
        Assert.Empty(sap.ClosedRequests);
    }

    /// <summary>
    /// Adding moves stock out of the source warehouse, so who may add mirrors who may convert a transfer
    /// request: a depot controller only from its own warehouses — the header's and every line's.
    /// </summary>
    [Fact]
    public async Task A_depot_controller_adds_only_what_leaves_its_own_warehouses()
    {
        var depot = await UserAsync(ApplicationRoles.DepotController, "CMFS");

        var foreign = new FakeTransferSap(HeldTransfers.Draft(79744, "CMFC", "DISPYOG"), HeldTransfers.Request(74045, 79744));
        var foreignResult = await Handler(foreign).Handle(Command(79744, depot), CancellationToken.None);
        Assert.Equal("InventoryTransfer.WarehouseNotAssigned", foreignResult.FirstError.Code);
        Assert.Empty(foreign.Saves);

        var mixed = new FakeTransferSap(
            HeldTransfers.Draft(77717, "CMFS", "CMFC",
                HeldTransfers.Line(0, "CHE001", 10m, "CMFS", "CMFC", ("CHE001/A", 10m)),
                HeldTransfers.Line(1, "CHE011", 5m, "CMFC", "CMFS", ("CHE011/B", 5m))),
            HeldTransfers.Request(72043, 77717));
        var mixedResult = await Handler(mixed).Handle(Command(77717, depot), CancellationToken.None);
        Assert.Equal("InventoryTransfer.WarehouseNotAssigned", mixedResult.FirstError.Code);
        Assert.Empty(mixed.Saves);

        var own = new FakeTransferSap(HeldTransfers.Draft(77664, "CMFS", "CMFC"), HeldTransfers.Request(71993, 77664));
        var ownResult = await Handler(own).Handle(Command(77664, depot), CancellationToken.None);
        Assert.False(ownResult.IsError, Describe(ownResult));
        Assert.Single(own.Saves);
    }

    [Fact]
    public async Task The_wash_bay_adds_from_anywhere_and_a_cashier_adds_nothing()
    {
        var washBay = await UserAsync(ApplicationRoles.WashBay);
        var cashier = await UserAsync(ApplicationRoles.Cashier);

        var sap = new FakeTransferSap(HeldTransfers.Draft(79744, "CMFC", "DISPYOG"), HeldTransfers.Request(74045, 79744));
        var cashierResult = await Handler(sap).Handle(Command(79744, cashier), CancellationToken.None);
        Assert.Equal("InventoryTransfer.TransferRequestConverterRoleRequired", cashierResult.FirstError.Code);
        Assert.Empty(sap.Saves);

        var washBayResult = await Handler(sap).Handle(Command(79744, washBay), CancellationToken.None);
        Assert.False(washBayResult.IsError, Describe(washBayResult));
        Assert.Single(sap.Saves);
    }

    /// <summary>A lost answer is settled by the draft: closed means the transfer exists, and the add is reported as done.</summary>
    [Fact]
    public async Task A_lost_answer_after_sap_converted_the_draft_is_a_success()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(79744, "CMFC", "DISPYOG");
        var request = HeldTransfers.Request(74045, 79744);
        var sap = new FakeTransferSap(draft, request)
        {
            ThrowOnSave = new TaskCanceledException("The request timed out"),
            ThrownSaveLanded = true,
            RequestsAfterSave = [request.Generated(151600)]
        };
        sap.Transfers[151600] = HeldTransfers.TransferLike(draft, 151600, 112400);

        var result = await Handler(sap).Handle(Command(79744, admin), CancellationToken.None);

        Assert.False(result.IsError, Describe(result));
        Assert.Equal(112400, result.Value.TransferDocNum);
    }

    /// <summary>
    /// A lost answer with the draft still open is uncertain, and says so. The claim is released, so the
    /// next click asks again — and finds the draft open, so it adds; a closed one it would refuse.
    /// </summary>
    [Fact]
    public async Task A_lost_answer_with_the_draft_still_open_is_uncertain_and_the_next_click_can_retry()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var sap = new FakeTransferSap(HeldTransfers.Draft(79744, "CMFC", "DISPYOG"), HeldTransfers.Request(74045, 79744))
        {
            ThrowOnSave = new HttpRequestException("Connection reset"),
            ThrownSaveLanded = false,
            RequestsAfterSave = []
        };
        var handler = Handler(sap);

        var first = await handler.Handle(Command(79744, admin), CancellationToken.None);
        Assert.Equal("SapTransferApproval.AddUncertain", first.FirstError.Code);
        Assert.Empty(sap.Saves);

        var second = await handler.Handle(Command(79744, admin), CancellationToken.None);
        Assert.False(second.IsError, Describe(second));
        Assert.Single(sap.Saves);
    }

    /// <summary>A repeat after a success replays the first answer and never reaches SAP a second time.</summary>
    [Fact]
    public async Task A_repeat_after_a_success_replays_the_first_answer_without_a_second_save()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var colleague = await UserAsync(ApplicationRoles.StockController);
        var draft = HeldTransfers.Draft(79744, "CMFC", "DISPYOG");
        var request = HeldTransfers.Request(74045, 79744);
        var sap = new FakeTransferSap(draft, request) { RequestsAfterSave = [request.Generated(151600)] };
        sap.Transfers[151600] = HeldTransfers.TransferLike(draft, 151600, 112400);

        var first = await Handler(sap).Handle(Command(79744, admin), CancellationToken.None);
        var second = await Handler(sap).Handle(Command(79744, colleague), CancellationToken.None);

        Assert.False(second.IsError, Describe(second));
        Assert.Single(sap.Saves);
        Assert.Equal(first.Value.TransferDocNum, second.Value.TransferDocNum);
        Assert.Equal(first.Value.Message, second.Value.Message);
    }

    [Fact]
    public async Task A_replay_is_refused_to_a_depot_controller_from_another_warehouse()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var depot = await UserAsync(ApplicationRoles.DepotController, "CMFS");
        var sap = new FakeTransferSap(HeldTransfers.Draft(79744, "CMFC", "DISPYOG"), HeldTransfers.Request(74045, 79744));

        Assert.False((await Handler(sap).Handle(Command(79744, admin), CancellationToken.None)).IsError);
        var replay = await Handler(sap).Handle(Command(79744, depot), CancellationToken.None);

        Assert.Equal("InventoryTransfer.WarehouseNotAssigned", replay.FirstError.Code);
    }

    /// <summary>
    /// A draft copied from a transfer request in the B1 client does not close that request. Left open,
    /// the request could be converted here and move the same stock again, so the add closes it and marks
    /// the app's own approval of it generated.
    /// </summary>
    [Fact]
    public async Task The_transfer_request_a_draft_was_copied_from_is_closed_after_the_add()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(
            78464, "CMFC", "CMFS",
            HeldTransfers.Line(2, "GOU016", 40m, "CMFC", "CMFS", ("GOU016/J18", 40m)).CopiedFrom(22788),
            HeldTransfers.Line(21, "CHE030", 50m, "CMFC", "CMFS", ("CHE030/L21", 50m)));
        var request = HeldTransfers.Request(72754, 78464);
        var sap = new FakeTransferSap(draft, request) { RequestsAfterSave = [request.Generated(151700)] };
        sap.Transfers[151700] = HeldTransfers.TransferLike(draft, 151700, 112500);
        sap.TransferRequests[22788] = HeldTransfers.OpenRequest(22788, 10688, "CMFC", "CMFS");
        var approvals = new RecordingTransferApprovals();

        var result = await Handler(sap, approvals: approvals).Handle(Command(78464, admin), CancellationToken.None);

        Assert.False(result.IsError, Describe(result));
        Assert.Equal([22788], sap.ClosedRequests);
        Assert.Equal([10688], result.Value.ClosedRequestDocNums);
        Assert.Empty(result.Value.RequestsLeftOpen);
        Assert.Contains("Transfer request #10688 is closed", result.Value.Message);

        var generated = Assert.Single(approvals.Generated);
        Assert.Equal((22788, 151700, 112500), (generated.RequestDocEntry, generated.TransferDocEntry, generated.TransferDocNum));
    }

    /// <summary>
    /// A draft a conversion here saved for SAP's approval copies nothing in SAP's sense — it is posted
    /// fresh, as the conversion posts — and names its request by the <c>ITR-{DocEntry}</c> marker
    /// instead. Adding it must close that request just the same.
    /// </summary>
    [Fact]
    public async Task The_request_a_conversion_sent_to_SAP_for_approval_is_closed_after_the_add()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(
            90001, "CMB", "CMFS",
            HeldTransfers.Line(0, "ICA004", 20m, "CMB", "CMFS"));
        draft.U_OrderNumber = InventoryTransferRequestConversion.OrderNumberFor(4101);
        var request = HeldTransfers.Request(80001, 90001);
        var sap = new FakeTransferSap(draft, request) { RequestsAfterSave = [request.Generated(151800)] };
        sap.Transfers[151800] = HeldTransfers.TransferLike(draft, 151800, 112600);
        sap.TransferRequests[4101] = HeldTransfers.OpenRequest(4101, 10834, "CMB", "CMFS");
        var approvals = new RecordingTransferApprovals();

        var result = await Handler(sap, approvals: approvals).Handle(Command(90001, admin), CancellationToken.None);

        Assert.False(result.IsError, Describe(result));
        Assert.Equal([4101], sap.ClosedRequests);
        Assert.Equal([10834], result.Value.ClosedRequestDocNums);
        var generated = Assert.Single(approvals.Generated);
        Assert.Equal((4101, 151800, 112600), (generated.RequestDocEntry, generated.TransferDocEntry, generated.TransferDocNum));
    }

    [Fact]
    public async Task A_request_sap_closed_itself_is_not_closed_twice_and_one_that_will_not_close_is_said()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(
            70898, "CMFS", "CMB",
            HeldTransfers.Line(0, "DAI003", 1200m, "CMFS", "CMB", ("DAI003/A", 1200m)).CopiedFrom(21856),
            HeldTransfers.Line(1, "DAI004", 1200m, "CMFS", "CMB", ("DAI004/A", 1200m)).CopiedFrom(21859));
        var sap = new FakeTransferSap(draft, HeldTransfers.Request(70898, 70898))
        {
            ClosedBySapOnSave = [21856],
            CloseFails = [21859]
        };
        sap.TransferRequests[21856] = HeldTransfers.OpenRequest(21856, 10233, "CMFS", "CMB");
        sap.TransferRequests[21859] = HeldTransfers.OpenRequest(21859, 10235, "CMFS", "CMB");

        var result = await Handler(sap).Handle(Command(70898, admin), CancellationToken.None);

        Assert.False(result.IsError, "the stock has moved; a request that will not close is reported, not failed");
        Assert.Empty(sap.ClosedRequests);
        Assert.Equal([10233], result.Value.ClosedRequestDocNums);
        Assert.Contains(result.Value.RequestsLeftOpen, line => line.Contains("#10235") && line.Contains("Transfers page"));
        Assert.Contains("#10235", result.Value.Message);
    }

    /// <summary>
    /// The request a draft was copied from may already have been converted here — its transfer carries
    /// <c>ITR-{DocEntry}</c>. Adding the draft too would move the same stock a second time.
    /// </summary>
    [Fact]
    public async Task A_draft_whose_request_was_already_converted_here_is_refused()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(
            67716, "CMFC", "DISPYOG",
            HeldTransfers.Line(0, "YOG010", 20m, "CMFC", "DISPYOG", ("YOG010/A", 20m)).CopiedFrom(16972));
        var sap = new FakeTransferSap(draft, HeldTransfers.Request(62253, 67716));
        sap.TransferRequests[16972] = HeldTransfers.OpenRequest(16972, 7826, "CMFC", "DISPYOG");
        sap.AppConversions[InventoryTransferRequestConversion.OrderNumberFor(16972)] = new InventoryTransfer { DocEntry = 99001, DocNum = 88001 };

        var result = await Handler(sap).Handle(Command(67716, admin), CancellationToken.None);

        Assert.Equal("SapTransferApproval.AlreadyTransferredByApp", result.FirstError.Code);
        Assert.Contains("#7826", result.FirstError.Description);
        Assert.Contains("#88001", result.FirstError.Description);
        Assert.Empty(sap.Saves);
    }

    [Fact]
    public async Task A_conversion_of_the_same_request_in_flight_blocks_the_add()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(
            67716, "CMFC", "DISPYOG",
            HeldTransfers.Line(0, "YOG010", 20m, "CMFC", "DISPYOG", ("YOG010/A", 20m)).CopiedFrom(16972));
        var sap = new FakeTransferSap(draft, HeldTransfers.Request(62253, 67716));
        sap.TransferRequests[16972] = HeldTransfers.OpenRequest(16972, 7826, "CMFC", "DISPYOG");

        // What ConvertTransferRequestHandler holds while it posts a conversion of request 16972.
        var held = await _store.TryAcquireAsync<int>(
            ConvertTransferRequestHandler.ConversionPostScope, "16972", new { DocEntry = 16972 }, CancellationToken.None);
        Assert.Equal(IdempotencyAcquireOutcome.Acquired, held.Outcome);

        var blocked = await Handler(sap).Handle(Command(67716, admin), CancellationToken.None);
        Assert.Equal("SapTransferApproval.ConversionInProgress", blocked.FirstError.Code);
        Assert.Empty(sap.Saves);

        // The add let go of the draft's claim, so once the conversion lock is released it goes through.
        await _store.ReleaseAsync(held.RequestId!.Value, CancellationToken.None);
        var after = await Handler(sap).Handle(Command(67716, admin), CancellationToken.None);
        Assert.False(after.IsError, Describe(after));

        // And it releases the conversion lock it took.
        var free = await _store.TryAcquireAsync<int>(
            ConvertTransferRequestHandler.ConversionPostScope, "16972", new { DocEntry = 16972 }, CancellationToken.None);
        Assert.Equal(IdempotencyAcquireOutcome.Acquired, free.Outcome);
    }

    /// <summary>
    /// The check a conversion makes: a transfer out of a till's warehouse must not take units the till
    /// sold that SAP has not invoiced yet. Asked only when a till has such a sale of one of its items.
    /// </summary>
    [Fact]
    public async Task Units_a_till_has_sold_but_sap_has_not_invoiced_are_not_taken()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(89450, "KEFGRC", "VAN002",
            HeldTransfers.Line(0, "YOG145", 685m, "KEFGRC", "VAN002", ("YOG145/A", 685m)));
        var claims = new FixedTillClaims(new() { ["KEFGRC"] = new() { ["YOG145"] = 8m } });

        var shortSap = new FakeTransferSap(draft, HeldTransfers.Request(89000, 89450));
        var shortage = new StockValidationResult();
        shortage.Errors.Add(new StockValidationError
        {
            LineNumber = 1,
            ItemCode = "YOG145",
            WarehouseCode = "KEFGRC",
            RequestedQuantity = 685m,
            AvailableQuantity = 677m
        });
        var refused = await Handler(shortSap, claims: claims, stock: new FixedStockValidation(shortage))
            .Handle(Command(89450, admin), CancellationToken.None);
        Assert.Equal("SapTransferApproval.TillSalesWouldBeTaken", refused.FirstError.Code);
        Assert.Contains("YOG145", refused.FirstError.Description);
        Assert.Empty(shortSap.Saves);

        var unreadSap = new FakeTransferSap(draft, HeldTransfers.Request(89000, 89450));
        var unread = new StockValidationResult();
        unread.UnreadableWarehouses.Add("KEFGRC");
        var unreadResult = await Handler(unreadSap, claims: claims, stock: new FixedStockValidation(unread))
            .Handle(Command(89450, admin), CancellationToken.None);
        Assert.Equal("SapTransferApproval.StockUnreadable", unreadResult.FirstError.Code);
        Assert.Empty(unreadSap.Saves);

        // A warehouse no till sells from is never stock-checked at all.
        var quiet = new FixedStockValidation(shortage);
        var plain = new FakeTransferSap(HeldTransfers.Draft(79744, "CMFC", "DISPYOG"), HeldTransfers.Request(74045, 79744));
        Assert.False((await Handler(plain, claims: claims, stock: quiet).Handle(Command(79744, admin), CancellationToken.None)).IsError);
        Assert.Equal(0, quiet.Calls);
    }

    [Fact]
    public async Task A_cancelled_request_reaches_nothing()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var sap = new FakeTransferSap(HeldTransfers.Draft(79744, "CMFC", "DISPYOG"), HeldTransfers.Request(74045, 79744));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await Handler(sap).Handle(Command(79744, admin), cancelled.Token);

        Assert.Equal("SapTransferApproval.Cancelled", result.FirstError.Code);
        Assert.Empty(sap.Saves);
    }

    private AddApprovedSapTransferHandler Handler(
        FakeTransferSap sap,
        RecordingAuditService? audit = null,
        RecordingTransferApprovals? approvals = null,
        FixedTillClaims? claims = null,
        FixedStockValidation? stock = null) => new(
        sap.AsClient(),
        new TransferWarehouseAuthorizer(_context),
        (approvals ?? new RecordingTransferApprovals()).AsService(),
        claims ?? new FixedTillClaims(),
        (stock ?? new FixedStockValidation(new StockValidationResult())).AsService(),
        _store,
        audit ?? new RecordingAuditService(),
        Options.Create(new SAPSettings { Enabled = true }),
        NullLogger<AddApprovedSapTransferHandler>.Instance);

    private static AddApprovedSapTransferCommand Command(int draftEntry, User user) => new(draftEntry, user.Id, user.Username, null);

    private async Task<User> UserAsync(string role, params string[] warehouses)
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
        user.SetWarehouseCodes(warehouses.ToList());
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private static string Describe<T>(ErrorOr.ErrorOr<T> result) =>
        result.IsError ? string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Description}")) : "ok";
}
