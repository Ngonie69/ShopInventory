using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.SapTransferApprovals;
using ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApproval;
using ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The held-transfer queue and one draft in it: who sees which rows, how the requests are joined and
/// named, and what the detail can say that the list cannot — every line's batch pick.
/// </summary>
public sealed class SapTransferApprovalQueueTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public SapTransferApprovalQueueTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Who may see the queue is who may convert a transfer request — the same five roles. Held together
    /// here so a role added to one cannot quietly miss the other.
    /// </summary>
    [Fact]
    public async Task The_queue_admits_exactly_the_roles_that_may_convert_a_transfer_request()
    {
        var authorizer = new TransferWarehouseAuthorizer(_context);
        var access = new SapTransferApprovalAccess(_context, authorizer);

        foreach (var role in HeldTransfers.RoleNames())
        {
            var user = await UserAsync(role, "CMFS");
            var mayConvert = !(await authorizer.EnsureCanConvertRequestAsync(user.Id, "CMFS", CancellationToken.None)).IsError;
            var maySee = !(await access.ResolveAsync(user.Id, CancellationToken.None)).IsError;

            Assert.True(mayConvert == maySee, $"{role}: may convert {mayConvert}, may see the held transfers {maySee}");
        }
    }

    [Fact]
    public async Task A_scoped_account_sees_its_own_warehouses_and_one_with_none_sees_nothing()
    {
        var access = new SapTransferApprovalAccess(_context, new TransferWarehouseAuthorizer(_context));
        var depot = await UserAsync(ApplicationRoles.DepotController, "CMFS", "CMB");
        var empty = await UserAsync(ApplicationRoles.ShopStock);
        var admin = await UserAsync(ApplicationRoles.Admin);

        Assert.Equal(["CMFS", "CMB"], (await access.ResolveAsync(depot.Id, CancellationToken.None)).Value.SourceWarehouses);
        Assert.Equal("SapTransferApproval.NoAssignedWarehouses", (await access.ResolveAsync(empty.Id, CancellationToken.None)).FirstError.Code);
        Assert.Null((await access.ResolveAsync(admin.Id, CancellationToken.None)).Value.SourceWarehouses);
        Assert.Null((await access.ResolveAsync(null, CancellationToken.None)).Value.SourceWarehouses);
    }

    /// <summary>
    /// A depot controller's queue is filtered in SAP — so the count and the pages are of their queue —
    /// rather than thinned out of somebody else's page here.
    /// </summary>
    [Fact]
    public async Task The_list_passes_the_callers_warehouses_to_sap_and_joins_the_requests()
    {
        var depot = await UserAsync(ApplicationRoles.DepotController, "CMFS");
        var first = HeldTransfers.Draft(77717, "CMFS", "CMFC");
        var second = HeldTransfers.Draft(77664, "CMFS", "CMFC");
        var sap = new FakeTransferSap(first,
            HeldTransfers.Request(72043, 77717),
            HeldTransfers.Request(71993, 77664),
            HeldTransfers.Request(71994, 77664, SapApprovalRequestStatuses.Pending))
        {
            ListDrafts = [first, second],
            ListTotal = 48
        };
        var lookups = FakeSapApprovalLookups.WithStage(15, "Factory Outgoing", [new SAPUser { InternalKey = 48, UserCode = "Stores", UserName = "Stores Clerk" }]);
        lookups.Templates[72] = new SAPApprovalTemplate { Code = 72, Name = "CMFS transfers" };

        var result = await new GetSapTransferApprovalsHandler(
                sap.AsClient(), lookups, Access(), Options.Create(new SAPSettings { Enabled = true }),
                NullLogger<GetSapTransferApprovalsHandler>.Instance)
            .Handle(new GetSapTransferApprovalsQuery(1, 2, null, "cmfc", depot.Id), CancellationToken.None);

        Assert.False(result.IsError);
        var read = Assert.Single(sap.ListReads);
        Assert.Equal(["CMFS"], read.Scope);
        Assert.Equal("cmfc", read.Filter?.Warehouse);

        Assert.Equal(48, result.Value.TotalCount);
        Assert.Equal(["CMFS"], result.Value.ScopedWarehouses);
        Assert.Equal(77664, result.Value.NextCursor);

        var row = result.Value.Items[0];
        Assert.Equal(72043, row.RequestCode);
        Assert.Equal("Factory Outgoing", row.StageName);
        Assert.Equal("CMFS transfers", row.TemplateName);
        Assert.Equal("Stores Clerk", row.OriginatorName);
        Assert.True(row.CanAdd);

        // Two templates matched the second draft and one is still pending: SAP will not take it yet.
        var held = result.Value.Items[1];
        Assert.Equal([71994, 71993], held.RequestCodes);
        Assert.False(held.CanAdd);
        Assert.Contains("71994", held.StatusNote);
    }

    [Fact]
    public async Task An_administrators_list_is_not_scoped_and_a_short_page_ends_the_queue()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var only = HeldTransfers.Draft(80083, "PROYG", "EXPHW");
        var sap = new FakeTransferSap(only, HeldTransfers.Request(74352, 80083)) { ListDrafts = [only], ListTotal = 1 };

        var result = await new GetSapTransferApprovalsHandler(
                sap.AsClient(), new FakeSapApprovalLookups(), Access(), Options.Create(new SAPSettings { Enabled = true }),
                NullLogger<GetSapTransferApprovalsHandler>.Instance)
            .Handle(new GetSapTransferApprovalsQuery(1, 25, CallerUserId: admin.Id), CancellationToken.None);

        Assert.Null(Assert.Single(sap.ListReads).Scope);
        Assert.Null(result.Value.ScopedWarehouses);
        Assert.Null(result.Value.NextCursor);
    }

    /// <summary>
    /// The list sees only the draft's header; the detail reads the draft by key, which is the only read
    /// SAP answers with each line's batch picks, so it can say which line SAP would refuse.
    /// </summary>
    [Fact]
    public async Task The_detail_flags_a_line_without_a_batch_pick_and_withholds_the_add()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var draft = HeldTransfers.Draft(
            63928, "CMFC", "CMFS",
            HeldTransfers.Line(0, "CHE001", 20m, "CMFC", "CMFS").CopiedFrom(14808),
            HeldTransfers.Line(1, "CHE011", 90m, "CMFC", "CMFS", ("CHE011/A", 90m)).CopiedFrom(14808));
        var sap = new FakeTransferSap(draft, HeldTransfers.Request(58563, 63928))
        {
            Management = HeldTransfers.BatchManaged("CHE001", "CHE011")
        };
        sap.TransferRequests[14808] = HeldTransfers.OpenRequest(14808, 6806, "CMFC", "CMFS");

        var result = await Detail(sap).Handle(new GetSapTransferApprovalQuery(63928, admin.Id), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.False(result.Value.Draft.CanAdd);
        Assert.Contains("CHE001 (20) has no batch picked", result.Value.Draft.StatusNote);
        Assert.True(result.Value.Lines[0].SelectionMissing);
        Assert.False(result.Value.Lines[1].SelectionMissing);
        Assert.Equal("CHE011/A", Assert.Single(result.Value.Lines[1].Batches).BatchNumber);

        var linked = Assert.Single(result.Value.LinkedRequests);
        Assert.Equal((14808, 6806, "Open"), (linked.DocEntry, linked.DocNum!.Value, linked.Status));
        Assert.False(result.Value.SelectionUnchecked);
    }

    [Fact]
    public async Task The_detail_says_when_it_could_not_check_the_picks_and_leaves_the_add_to_sap()
    {
        var admin = await UserAsync(ApplicationRoles.Admin);
        var sap = new FakeTransferSap(HeldTransfers.Draft(79744, "CMFC", "DISPYOG"), HeldTransfers.Request(74045, 79744)) { Management = null };

        var result = await Detail(sap).Handle(new GetSapTransferApprovalQuery(79744, admin.Id), CancellationToken.None);

        Assert.True(result.Value.SelectionUnchecked);
        Assert.True(result.Value.Draft.CanAdd);
        Assert.Null(result.Value.Lines[0].ManagesBatches);
    }

    [Fact]
    public async Task A_draft_out_of_another_warehouse_is_not_described_to_a_depot_controller()
    {
        var depot = await UserAsync(ApplicationRoles.DepotController, "CMFS");
        var sap = new FakeTransferSap(HeldTransfers.Draft(79744, "CMFC", "DISPYOG"), HeldTransfers.Request(74045, 79744));

        var result = await Detail(sap).Handle(new GetSapTransferApprovalQuery(79744, depot.Id), CancellationToken.None);

        Assert.Equal("SapTransferApproval.OutsideWarehouseScope", result.FirstError.Code);
        Assert.Equal(ErrorOr.ErrorType.Forbidden, result.FirstError.Type);
    }

    private GetSapTransferApprovalHandler Detail(FakeTransferSap sap) => new(
        sap.AsClient(),
        new FakeSapApprovalLookups(),
        Access(),
        Options.Create(new SAPSettings { Enabled = true }),
        NullLogger<GetSapTransferApprovalHandler>.Instance);

    private SapTransferApprovalAccess Access() => new(_context, new TransferWarehouseAuthorizer(_context));

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
}
