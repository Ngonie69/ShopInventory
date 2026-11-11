using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.SapTransferApprovals;
using ShopInventory.Features.SapTransferApprovals.Commands.AddApprovedSapTransfer;
using ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApproval;
using ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Transfer request drafts on the SAP-held transfers page: SAP keeps them in the same draft set as the
/// transfers, under their own object code. The queue reads either kind or both and counts each; adding a
/// request draft makes an open transfer request and moves no stock, so none of the transfer's stock
/// checks apply to it.
/// </summary>
public sealed class SapTransferRequestDraftTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly IIdempotencyRequestStore _store;

    public SapTransferRequestDraftTests()
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

    // ── The OData ─────────────────────────────────────────────────────────

    [Fact]
    public void The_kind_decides_which_object_codes_the_read_admits()
    {
        Assert.Equal("DocObjectCode eq '67'", SAPServiceLayerClient.DraftKindClause(SapHeldDraftKind.Transfer));
        Assert.Equal("DocObjectCode eq '1250000001'", SAPServiceLayerClient.DraftKindClause(SapHeldDraftKind.Request));
        Assert.Equal(
            "(DocObjectCode eq '67' or DocObjectCode eq '1250000001')",
            SAPServiceLayerClient.DraftKindClause(null));
    }

    /// <summary>The list and the kind switch's count are built from one filter, so they cannot disagree.</summary>
    [Fact]
    public void The_queue_filter_puts_the_kind_first_and_keeps_the_scope_and_page_filters()
    {
        var filter = SAPServiceLayerClient.ApprovedDraftQueueFilter(
            ["CMFC"],
            new SapTransferDraftFilter { Kind = SapHeldDraftKind.Request, AttachedOnly = true });

        Assert.Equal(
            "DocObjectCode eq '1250000001'"
            + " and DocumentStatus eq 'bost_Open'"
            + " and AuthorizationStatus eq 'sasApproved'"
            + " and (FromWarehouse eq 'CMFC')"
            + " and AttachmentEntry gt 0",
            filter);
    }

    [Fact]
    public void A_kind_outside_the_enum_is_refused()
    {
        var validator = new GetSapTransferApprovalsValidator();

        Assert.True(validator.Validate(new GetSapTransferApprovalsQuery(1, 25, Filter: new SapTransferDraftFilter { Kind = SapHeldDraftKind.Request })).IsValid);
        Assert.False(validator.Validate(new GetSapTransferApprovalsQuery(1, 25, Filter: new SapTransferDraftFilter { Kind = (SapHeldDraftKind)7 })).IsValid);
    }

    // ── The queue ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Reading_requests_counts_the_transfers_too_and_each_row_names_its_kind()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var request = HeldTransfers.RequestDraft(80412, "CMFC", "DISPYOG");
        var sap = new FakeTransferSap(request, HeldTransfers.Request(75102, 80412))
        {
            ListDrafts = [request],
            ListTotal = 4,
            KindCount = 27
        };

        var result = await List(sap).Handle(
            new GetSapTransferApprovalsQuery(1, 25, CallerUserId: admin.Id, Filter: new SapTransferDraftFilter { Kind = SapHeldDraftKind.Request }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(SapHeldDraftKind.Request, Assert.Single(sap.ListReads).Filter?.Kind);
        Assert.Equal(SapHeldDraftKind.Transfer, Assert.Single(sap.CountReads)?.Kind);
        Assert.Equal((27, 4), (result.Value.TransferCount, result.Value.RequestCount));
        Assert.Equal(4, result.Value.TotalCount);

        var row = Assert.Single(result.Value.Items);
        Assert.Equal("Request", row.Kind);
        Assert.True(row.CanAdd);
    }

    [Fact]
    public async Task Reading_both_kinds_counts_the_requests_and_the_transfers_are_the_rest()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var transfer = HeldTransfers.Draft(80398, "DISPCH", "KEFSHOP");
        var request = HeldTransfers.RequestDraft(80412, "CMFC", "DISPYOG");
        var sap = new FakeTransferSap(transfer, HeldTransfers.Request(75090, 80398), HeldTransfers.Request(75102, 80412))
        {
            ListDrafts = [request, transfer],
            ListTotal = 31,
            KindCount = 4
        };

        var result = await List(sap).Handle(new GetSapTransferApprovalsQuery(1, 25, CallerUserId: admin.Id), CancellationToken.None);

        Assert.Null(Assert.Single(sap.ListReads).Filter?.Kind);
        Assert.Equal(SapHeldDraftKind.Request, Assert.Single(sap.CountReads)?.Kind);
        Assert.Equal((27, 4), (result.Value.TransferCount, result.Value.RequestCount));
        Assert.Equal(["Request", "Transfer"], result.Value.Items.Select(item => item.Kind));
    }

    [Fact]
    public async Task A_count_that_fails_costs_the_switch_its_numbers_not_the_page_its_rows()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var request = HeldTransfers.RequestDraft(80412, "CMFC", "DISPYOG");
        var sap = new FakeTransferSap(request, HeldTransfers.Request(75102, 80412))
        {
            ListDrafts = [request],
            ListTotal = 1,
            KindCount = null
        };

        var result = await List(sap).Handle(new GetSapTransferApprovalsQuery(1, 25, CallerUserId: admin.Id), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Single(result.Value.Items);
        Assert.Null(result.Value.TransferCount);
        Assert.Null(result.Value.RequestCount);
    }

    [Fact]
    public void The_counts_are_never_negative_when_an_add_lands_between_the_two_reads()
    {
        Assert.Equal((0, 5), GetSapTransferApprovalsHandler.KindCounts(null, 4, 5));
        Assert.Equal((12, 3), GetSapTransferApprovalsHandler.KindCounts(SapHeldDraftKind.Transfer, 12, 3));
        Assert.Equal(((int?)null, 3), GetSapTransferApprovalsHandler.KindCounts(SapHeldDraftKind.Request, 3, null));
    }

    // ── The detail ────────────────────────────────────────────────────────

    /// <summary>
    /// A request carries no picks, so the item flags are not read and nothing is said about batches. It
    /// was copied from no request, so none is listed.
    /// </summary>
    [Fact]
    public async Task The_detail_of_a_request_draft_reads_no_batch_flags()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var sap = new FakeTransferSap(HeldTransfers.RequestDraft(80412, "CMFC", "DISPYOG"), HeldTransfers.Request(75102, 80412))
        {
            Management = null
        };

        var result = await Detail(sap).Handle(new GetSapTransferApprovalQuery(80412, admin.Id), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Request", result.Value.Draft.Kind);
        Assert.True(result.Value.Draft.CanAdd);
        Assert.Equal(0, sap.ManagementReads);
        Assert.False(result.Value.SelectionUnchecked);
        Assert.Empty(result.Value.LinkedRequests);
        Assert.False(Assert.Single(result.Value.Lines).SelectionMissing);
    }

    [Fact]
    public async Task A_request_for_another_warehouses_stock_is_not_described_to_a_depot_controller()
    {
        var depot = await UserAsync(ApplicationRoles.DepotController, "CMFS");
        var sap = new FakeTransferSap(HeldTransfers.RequestDraft(80412, "CMFC", "DISPYOG"), HeldTransfers.Request(75102, 80412));

        var result = await Detail(sap).Handle(new GetSapTransferApprovalQuery(80412, depot.Id), CancellationToken.None);

        Assert.Equal("SapTransferApproval.OutsideWarehouseScope", result.FirstError.Code);
        Assert.Contains("transfer request asks for stock from CMFC", result.FirstError.Description);
    }

    /// <summary>The set holds every kind of draft; anything but a transfer or a transfer request is not this page's.</summary>
    [Fact]
    public async Task A_draft_of_any_other_kind_is_not_found()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var receipt = HeldTransfers.Draft(80500, "CMFC", "DISPYOG");
        receipt.DocObjectCode = "20";
        var sap = new FakeTransferSap(receipt, HeldTransfers.Request(75200, 80500));

        Assert.Equal("SapTransferApproval.NotFound",
            (await Detail(sap).Handle(new GetSapTransferApprovalQuery(80500, admin.Id), CancellationToken.None)).FirstError.Code);
        Assert.Equal("SapTransferApproval.NotFound",
            (await Add(sap).Handle(Command(80500, admin), CancellationToken.None)).FirstError.Code);
        Assert.Empty(sap.Saves);
    }

    // ── The add ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adding a request draft makes the transfer request and moves nothing. None of the transfer's stock
    /// checks run: not the batch flags, not the till sales — though this warehouse's till has sold the
    /// item and a stock check would refuse it.
    /// </summary>
    [Fact]
    public async Task A_request_draft_is_added_as_a_transfer_request_with_no_stock_check()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.RequestDraft(80412, "CMFC", "DISPYOG");
        var sap = new FakeTransferSap(draft, HeldTransfers.Request(75102, 80412)) { SaveReturns = 20500, Management = null };
        sap.TransferRequests[20500] = HeldTransfers.RequestLike(draft, 20500, 10931);
        var claims = new FixedTillClaims(new() { ["CMFC"] = new() { ["YOG058"] = 8m } });
        var shortage = new StockValidationResult();
        shortage.Errors.Add(new StockValidationError { LineNumber = 1, ItemCode = "YOG058", WarehouseCode = "CMFC", RequestedQuantity = 48m, AvailableQuantity = 0m });
        var stock = new FixedStockValidation(shortage);
        var audit = new RecordingAuditService();

        var result = await Add(sap, audit, claims, stock).Handle(Command(80412, admin), CancellationToken.None);

        Assert.False(result.IsError, Describe(result));
        var save = Assert.Single(sap.Saves);
        Assert.Equal(80412, save.DraftEntry);
        Assert.False(save.Token.CanBeCanceled, "the save must run on a token the caller cannot cancel");

        Assert.Equal("Request", result.Value.Kind);
        Assert.True(result.Value.Resolved);
        Assert.Equal((20500, 10931), (result.Value.CreatedRequestDocEntry, result.Value.CreatedRequestDocNum));
        Assert.Null(result.Value.TransferDocEntry);
        Assert.Contains("Transfer request #10931", result.Value.Message);
        Assert.Contains("No stock has moved", result.Value.Message);

        Assert.Equal(0, sap.ManagementReads);
        Assert.Equal(0, stock.Calls);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AddApprovedSapTransfer, entry.Action);
        Assert.True(entry.Success);
        Assert.Contains("transfer request draft", entry.Details);
    }

    [Fact]
    public async Task When_sap_names_nothing_the_request_is_the_approval_requests_object_entry()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.RequestDraft(80412, "CMFC", "DISPYOG");
        var approval = HeldTransfers.Request(75102, 80412);
        var sap = new FakeTransferSap(draft, approval) { RequestsAfterSave = [approval.Generated(20500)] };
        sap.TransferRequests[20500] = HeldTransfers.RequestLike(draft, 20500, 10931);

        var result = await Add(sap).Handle(Command(80412, admin), CancellationToken.None);

        Assert.False(result.IsError, Describe(result));
        Assert.Equal(10931, result.Value.CreatedRequestDocNum);
    }

    /// <summary>
    /// With no answer and no request naming it, the request is the one raised since the add began
    /// between the same warehouses carrying exactly the draft's lines — never a DocNum match.
    /// </summary>
    [Fact]
    public async Task Failing_that_the_request_is_recognised_by_its_lines()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.RequestDraft(
            80412, "CMFC", "DISPYOG",
            HeldTransfers.Line(0, "YOG058", 48m, "CMFC", "DISPYOG"),
            HeldTransfers.Line(1, "YOG061", 24m, "CMFC", "DISPYOG"));
        var other = HeldTransfers.RequestLike(draft, 20501, 10932);
        other.StockTransferLines![1].Quantity = 23m;
        var ours = HeldTransfers.RequestLike(draft, 20502, 10933);
        var sap = new FakeTransferSap(draft, HeldTransfers.Request(75102, 80412))
        {
            RequestsAfterSave = [],
            NewestRequestBefore = 20500,
            RequestsAfter = [other, ours]
        };
        sap.TransferRequests[20502] = ours;

        var result = await Add(sap).Handle(Command(80412, admin), CancellationToken.None);

        Assert.False(result.IsError, Describe(result));
        Assert.True(result.Value.Resolved);
        Assert.Equal((20502, 10933), (result.Value.CreatedRequestDocEntry, result.Value.CreatedRequestDocNum));
    }

    [Fact]
    public async Task A_closed_request_draft_is_already_added_and_names_the_request()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = FakeTransferSap.Closed(HeldTransfers.RequestDraft(80412, "CMFC", "DISPYOG"));
        var sap = new FakeTransferSap(draft, HeldTransfers.Request(75102, 80412).Generated(20500));
        sap.TransferRequests[20500] = HeldTransfers.RequestLike(draft, 20500, 10931);

        var result = await Add(sap).Handle(Command(80412, admin), CancellationToken.None);

        Assert.Equal("SapTransferApproval.AlreadyAdded", result.FirstError.Code);
        Assert.Contains("transfer request #10931", result.FirstError.Description);
        Assert.Empty(sap.Saves);
    }

    [Fact]
    public async Task A_depot_controller_cannot_add_a_request_for_another_warehouses_stock()
    {
        var depot = await UserAsync(ApplicationRoles.DepotController, "CMFS");
        var sap = new FakeTransferSap(HeldTransfers.RequestDraft(80412, "CMFC", "DISPYOG"), HeldTransfers.Request(75102, 80412));

        var result = await Add(sap).Handle(Command(80412, depot), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(sap.Saves);
    }

    /// <summary>SAP's refusal of a request is said in its words, naming the request, not read as a missing batch pick.</summary>
    [Fact]
    public async Task Sap_refusing_a_request_draft_is_said_as_a_refused_request()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var sap = new FakeTransferSap(HeldTransfers.RequestDraft(80412, "CMFC", "DISPYOG"), HeldTransfers.Request(75102, 80412))
        {
            RefuseWith = "Cannot add row without complete selection of batch/serial numbers"
        };

        var result = await Add(sap).Handle(Command(80412, admin), CancellationToken.None);

        Assert.Equal("SapTransferApproval.SapRejected", result.FirstError.Code);
        Assert.Contains("refused the transfer request", result.FirstError.Description);
    }

    private GetSapTransferApprovalsHandler List(FakeTransferSap sap) => new(
        sap.AsClient(),
        new FakeSapApprovalLookups(),
        Access(),
        Options.Create(new SAPSettings { Enabled = true }),
        NullLogger<GetSapTransferApprovalsHandler>.Instance);

    private GetSapTransferApprovalHandler Detail(FakeTransferSap sap) => new(
        sap.AsClient(),
        new FakeSapApprovalLookups(),
        Access(),
        Options.Create(new SAPSettings { Enabled = true }),
        NullLogger<GetSapTransferApprovalHandler>.Instance);

    private AddApprovedSapTransferHandler Add(
        FakeTransferSap sap,
        RecordingAuditService? audit = null,
        FixedTillClaims? claims = null,
        FixedStockValidation? stock = null) => new(
        sap.AsClient(),
        new TransferWarehouseAuthorizer(_context),
        new RecordingTransferApprovals().AsService(),
        claims ?? new FixedTillClaims(),
        (stock ?? new FixedStockValidation(new StockValidationResult())).AsService(),
        _store,
        audit ?? new RecordingAuditService(),
        Options.Create(new SAPSettings { Enabled = true }),
        NullLogger<AddApprovedSapTransferHandler>.Instance);

    private SapTransferApprovalAccess Access() => new(_context, new TransferWarehouseAuthorizer(_context));

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
