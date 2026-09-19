using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.MarketBreakages.Commands.ConfirmMarketBreakage;
using ShopInventory.Features.MarketBreakages.Commands.RejectMarketBreakage;
using ShopInventory.Features.MarketBreakages.Commands.ReportMarketBreakage;
using ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakages;
using ShopInventory.Features.MarketBreakages.Queries.GetMyMarketBreakages;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A van rep reports broken stock collected from shops; the office counts it and confirms it into a
/// SAP transfer from the van to RETURNS. The report moves nothing, the count is what moves, and a
/// report is transferred at most once however many people press Confirm.
/// </summary>
public sealed class MarketBreakageTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherRep = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Office = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Unassigned = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;

    public MarketBreakageTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            NewUser(Rep, "rep1", "Sales", "Tendai", "Moyo", "VAN010"),
            NewUser(OtherRep, "rep2", "Sales", "Rudo", "Banda", "VAN011"),
            NewUser(Office, "office", "StockController", "Office", "Clerk", null),
            NewUser(Unassigned, "rep3", "Sales", null, null, null));
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- Reporting ---------------------------------------------------------------------------

    [Fact]
    public async Task A_report_is_recorded_against_the_reps_van_and_moves_nothing()
    {
        var result = await Report(Request("req-1", ("MILK500", 3m, "Broken"), ("YOG250", 2m, "Expired")));

        Assert.False(result.IsError);
        Assert.False(result.Value.AlreadyReported);

        var saved = await _context.MarketBreakages.AsNoTracking().Include(b => b.Lines).SingleAsync();
        Assert.Equal(MarketBreakageStatuses.Pending, saved.Status);
        Assert.Equal("VAN010", saved.VanWarehouseCode);
        Assert.Equal("Tendai Moyo", saved.ReportedByName);
        Assert.Equal("SHOP01", saved.CardCode);
        Assert.Equal([3m, 2m], saved.Lines.OrderBy(l => l.LineNum).Select(l => l.ReportedQuantity));
        Assert.All(saved.Lines, line => Assert.Null(line.ConfirmedQuantity));
        Assert.Null(saved.SapDocEntry);
    }

    [Fact]
    public async Task A_resend_with_the_same_id_answers_with_the_first_report()
    {
        var first = await Report(Request("req-1", ("MILK500", 3m, "Broken")));
        var second = await Report(Request("req-1", ("MILK500", 3m, "Broken")));

        Assert.False(second.IsError);
        Assert.True(second.Value.AlreadyReported);
        Assert.Equal(first.Value.Id, second.Value.Id);
        Assert.Equal(1, await _context.MarketBreakages.CountAsync());
    }

    [Fact]
    public async Task Another_reps_resend_of_the_same_id_is_refused_rather_than_shown_their_report()
    {
        await Report(Request("req-1", ("MILK500", 3m, "Broken")));

        var other = await Report(Request("req-1", ("MILK500", 3m, "Broken")), OtherRep);

        Assert.True(other.IsError);
        Assert.Equal("MarketBreakage.DuplicateRequest", other.FirstError.Code);
    }

    [Fact]
    public async Task A_rep_with_no_van_cannot_report_because_nothing_could_be_transferred()
    {
        var result = await Report(Request("req-1", ("MILK500", 3m, "Broken")), Unassigned);

        Assert.True(result.IsError);
        Assert.Equal("MarketBreakage.NoVanWarehouse", result.FirstError.Code);
        Assert.Equal(0, await _context.MarketBreakages.CountAsync());
    }

    [Fact]
    public async Task The_handset_list_holds_only_the_callers_own_reports()
    {
        await Report(Request("req-1", ("MILK500", 3m, "Broken")));
        await Report(Request("req-2", ("MILK500", 1m, "Broken")), OtherRep);

        var mine = await new GetMyMarketBreakagesHandler(_context)
            .Handle(new GetMyMarketBreakagesQuery(Rep), default);

        Assert.Single(mine.Value);
        Assert.Equal("VAN010", mine.Value[0].VanWarehouseCode);
    }

    [Fact]
    public void The_validator_refuses_an_empty_or_zero_report()
    {
        var validator = new ReportMarketBreakageValidator();

        Assert.False(validator.Validate(new ReportMarketBreakageCommand(Request("r"), Rep)).IsValid);
        Assert.False(validator.Validate(new ReportMarketBreakageCommand(Request("r", ("A", 0m, null)), Rep)).IsValid);
        Assert.False(validator.Validate(new ReportMarketBreakageCommand(Request("", ("A", 1m, null)), Rep)).IsValid);
        Assert.True(validator.Validate(new ReportMarketBreakageCommand(Request("r", ("A", 1m, null)), Rep)).IsValid);
    }

    // ---- Confirming --------------------------------------------------------------------------

    [Fact]
    public async Task Confirming_transfers_the_counted_quantities_from_the_van_to_returns()
    {
        var id = await GivenReportAsync(("MILK500", 3m), ("YOG250", 2m));
        var lines = await LineIdsAsync(id);
        var sap = new FakeSap();

        // The office found only 2 of the 3 milks and none of the yoghurt.
        var result = await Confirm(id, sap, (lines[0], 2m), (lines[1], 0m));

        Assert.False(result.IsError);
        var request = Assert.Single(sap.Requests);
        Assert.Equal("VAN010", request.FromWarehouse);
        Assert.Equal("RETURNS", request.ToWarehouse);
        var line = Assert.Single(request.Lines!);
        Assert.Equal(("MILK500", 2m), (line.ItemCode, line.Quantity));

        var saved = await ReloadAsync(id);
        Assert.Equal(MarketBreakageStatuses.Transferred, saved.Status);
        Assert.Equal(FakeSap.DocEntry, saved.SapDocEntry);
        Assert.Equal("RETURNS", saved.ReturnsWarehouseCode);
        Assert.Equal("Office Clerk", saved.DecidedByName);

        // What the rep said is kept beside what the office counted.
        var milk = saved.Lines.Single(l => l.ItemCode == "MILK500");
        Assert.Equal((3m, 2m), (milk.ReportedQuantity, milk.ConfirmedQuantity));
    }

    [Fact]
    public async Task The_returns_warehouse_comes_from_settings()
    {
        var id = await GivenReportAsync(("MILK500", 3m));
        var lines = await LineIdsAsync(id);
        var sap = new FakeSap();

        await Confirm(id, sap, returnsWarehouse: "RET-HRE", (lines[0], 3m));

        Assert.Equal("RET-HRE", Assert.Single(sap.Requests).ToWarehouse);
    }

    [Fact]
    public async Task A_count_of_all_zeros_is_refused_so_the_report_is_rejected_instead()
    {
        var id = await GivenReportAsync(("MILK500", 3m));
        var lines = await LineIdsAsync(id);
        var sap = new FakeSap();

        var result = await Confirm(id, sap, (lines[0], 0m));

        Assert.True(result.IsError);
        Assert.Equal("MarketBreakage.NothingToTransfer", result.FirstError.Code);
        Assert.Empty(sap.Requests);
        Assert.Equal(MarketBreakageStatuses.Pending, (await ReloadAsync(id)).Status);
    }

    [Fact]
    public async Task A_count_that_misses_a_line_is_refused()
    {
        var id = await GivenReportAsync(("MILK500", 3m), ("YOG250", 2m));
        var lines = await LineIdsAsync(id);
        var sap = new FakeSap();

        var result = await Confirm(id, sap, (lines[0], 3m));

        Assert.True(result.IsError);
        Assert.Equal("MarketBreakage.LinesMismatch", result.FirstError.Code);
        Assert.Empty(sap.Requests);
    }

    [Fact]
    public async Task A_second_confirm_after_the_transfer_replays_it_instead_of_moving_the_stock_twice()
    {
        var id = await GivenReportAsync(("MILK500", 3m));
        var lines = await LineIdsAsync(id);
        var sap = new FakeSap();

        await Confirm(id, sap, (lines[0], 3m));
        var again = await Confirm(id, sap, (lines[0], 3m));

        Assert.False(again.IsError);
        Assert.Equal(FakeSap.DocEntry, again.Value.Breakage.SapDocEntry);
        Assert.Single(sap.Requests);
    }

    [Fact]
    public async Task A_confirm_while_another_is_in_sap_is_refused()
    {
        var id = await GivenReportAsync(("MILK500", 3m));
        var lines = await LineIdsAsync(id);
        var sap = new FakeSap { Hold = true };
        var store = BuildStore();

        var first = Confirm(id, sap, store, _context, "RETURNS", (lines[0], 3m));
        await sap.EnteredSap.Task;

        await using var second = new ApplicationDbContext(_options);
        var refused = await Confirm(id, sap, store, second, "RETURNS", (lines[0], 3m));

        Assert.True(refused.IsError);
        Assert.Equal("MarketBreakage.PostInProgress", refused.FirstError.Code);

        sap.Release.SetResult();
        Assert.False((await first).IsError);
        Assert.Single(sap.Requests);
    }

    [Fact]
    public async Task A_refused_transfer_is_recorded_and_can_be_confirmed_again()
    {
        var id = await GivenReportAsync(("MILK500", 3m));
        var lines = await LineIdsAsync(id);
        var store = BuildStore();

        var failing = new FakeSap { Failure = new InvalidOperationException("Warehouse RETURNS does not exist") };
        var failed = await Confirm(id, failing, store, _context, "RETURNS", (lines[0], 3m));

        Assert.True(failed.IsError);
        var afterFailure = await ReloadAsync(id);
        Assert.Equal(MarketBreakageStatuses.TransferFailed, afterFailure.Status);
        Assert.Contains("does not exist", afterFailure.LastError);

        await using var retryContext = new ApplicationDbContext(_options);
        var retry = await Confirm(id, new FakeSap(), store, retryContext, "RETURNS", (lines[0], 3m));

        Assert.False(retry.IsError);
        Assert.Equal(MarketBreakageStatuses.Transferred, (await ReloadAsync(id)).Status);
    }

    [Fact]
    public async Task Short_stock_on_the_van_fails_the_transfer_without_calling_sap()
    {
        var id = await GivenReportAsync(("MILK500", 3m));
        var lines = await LineIdsAsync(id);
        var sap = new FakeSap();

        var result = await Confirm(id, sap, BuildStore(), _context, "RETURNS",
            StockValidationResult.Failure([new StockValidationError { ItemCode = "MILK500", WarehouseCode = "VAN010", RequestedQuantity = 3, AvailableQuantity = 2 }]),
            (lines[0], 3m));

        Assert.True(result.IsError);
        Assert.Equal("MarketBreakage.InsufficientStock", result.FirstError.Code);
        Assert.Empty(sap.Requests);
        Assert.Equal(MarketBreakageStatuses.TransferFailed, (await ReloadAsync(id)).Status);
    }

    // ---- Rejecting ---------------------------------------------------------------------------

    [Fact]
    public async Task Rejecting_records_why_and_moves_nothing()
    {
        var id = await GivenReportAsync(("MILK500", 3m));

        var result = await Reject(id, "Nothing came off the van");

        Assert.False(result.IsError);
        var saved = await ReloadAsync(id);
        Assert.Equal(MarketBreakageStatuses.Rejected, saved.Status);
        Assert.Equal("Nothing came off the van", saved.DecisionRemarks);
        Assert.Null(saved.SapDocEntry);
    }

    [Fact]
    public async Task A_transferred_report_cannot_be_rejected()
    {
        var id = await GivenReportAsync(("MILK500", 3m));
        var lines = await LineIdsAsync(id);
        await Confirm(id, new FakeSap(), (lines[0], 3m));

        var result = await Reject(id, "too late");

        Assert.True(result.IsError);
        Assert.Equal("MarketBreakage.NotActionable", result.FirstError.Code);
        Assert.Equal(MarketBreakageStatuses.Transferred, (await ReloadAsync(id)).Status);
    }

    [Fact]
    public async Task A_report_being_transferred_cannot_be_rejected()
    {
        var id = await GivenReportAsync(("MILK500", 3m));
        await _context.MarketBreakages.Where(b => b.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, MarketBreakageStatuses.Transferring));

        var result = await Reject(id, "changed my mind");

        Assert.True(result.IsError);
        Assert.Equal(MarketBreakageStatuses.Transferring, (await ReloadAsync(id)).Status);
    }

    [Fact]
    public async Task A_rejected_report_cannot_then_be_confirmed()
    {
        var id = await GivenReportAsync(("MILK500", 3m));
        var lines = await LineIdsAsync(id);
        await Reject(id, "not ours");
        var sap = new FakeSap();

        var result = await Confirm(id, sap, (lines[0], 3m));

        Assert.True(result.IsError);
        Assert.Equal("MarketBreakage.NotActionable", result.FirstError.Code);
        Assert.Empty(sap.Requests);
    }

    // ---- Office list -------------------------------------------------------------------------

    [Fact]
    public async Task The_office_list_filters_by_status_and_counts_every_tab()
    {
        var pending = await GivenReportAsync(("MILK500", 3m));
        var rejected = await GivenReportAsync(("YOG250", 1m));
        await Reject(rejected, "no");

        var open = await new GetMarketBreakagesHandler(_context)
            .Handle(new GetMarketBreakagesQuery("open", null, 1, 25), default);

        Assert.Equal(pending, Assert.Single(open.Value.Items).Id);
        Assert.Equal(1, open.Value.StatusCounts[MarketBreakageStatuses.Pending]);
        Assert.Equal(1, open.Value.StatusCounts[MarketBreakageStatuses.Rejected]);
        Assert.Equal(0, open.Value.StatusCounts[MarketBreakageStatuses.Transferred]);

        var search = await new GetMarketBreakagesHandler(_context)
            .Handle(new GetMarketBreakagesQuery(null, "yog", 1, 25), default);
        Assert.Equal(rejected, Assert.Single(search.Value.Items).Id);
    }

    // ---- Permissions -------------------------------------------------------------------------

    [Theory]
    [InlineData(ApplicationRoles.Sales, true, false)]
    [InlineData(ApplicationRoles.Adr, true, false)]
    [InlineData(ApplicationRoles.StockController, true, true)]
    [InlineData(ApplicationRoles.DepotController, false, true)]
    [InlineData(ApplicationRoles.Manager, false, true)]
    [InlineData(ApplicationRoles.Cashier, false, false)]
    [InlineData(ApplicationRoles.Driver, false, false)]
    public void Reps_report_and_the_office_confirms(string role, bool mayReport, bool mayConfirm)
    {
        var permissions = Permission.GetDefaultPermissionsForRole(role);

        Assert.Equal(mayReport, permissions.Contains(Permission.ReportMarketBreakages));
        Assert.Equal(mayConfirm, permissions.Contains(Permission.ConfirmMarketBreakages));
    }

    [Fact]
    public void Every_market_breakage_endpoint_demands_a_breakage_permission()
    {
        var actions = typeof(ShopInventory.Controllers.MarketBreakageController).GetMethods()
            .Where(method => method.DeclaringType == typeof(ShopInventory.Controllers.MarketBreakageController))
            .ToList();

        Assert.NotEmpty(actions);
        Assert.All(actions, action =>
        {
            var attribute = action.GetCustomAttributes(typeof(ShopInventory.Authentication.RequirePermissionAttribute), false)
                .Cast<ShopInventory.Authentication.RequirePermissionAttribute>()
                .SingleOrDefault();
            Assert.NotNull(attribute);
            Assert.Equal([Permission.ConfirmMarketBreakages], attribute.RequiredPermissions);
        });
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static User NewUser(Guid id, string username, string role, string? first, string? last, string? van)
    {
        var user = new User
        {
            Id = id,
            Username = username,
            PasswordHash = "x",
            Role = role,
            FirstName = first,
            LastName = last,
            IsActive = true
        };
        user.AssignedWarehouseCode = van;
        return user;
    }

    private static VanSalesMarketBreakageRequest Request(
        string clientRequestId,
        params (string Code, decimal Quantity, string? Reason)[] items) => new()
        {
            ClientRequestId = clientRequestId,
            CardCode = "SHOP01",
            CardName = "Corner Shop",
            CapturedAt = DateTime.UtcNow.AddMinutes(-5),
            Items = items.Select(item => new VanSalesMarketBreakageItem
            {
                Code = item.Code,
                Description = item.Code + " description",
                Quantity = item.Quantity,
                Reason = item.Reason
            }).ToList()
        };

    private Task<ErrorOr.ErrorOr<VanSalesMarketBreakageResponse>> Report(
        VanSalesMarketBreakageRequest request, Guid? userId = null)
        => new ReportMarketBreakageHandler(_context, NullLogger<ReportMarketBreakageHandler>.Instance)
            .Handle(new ReportMarketBreakageCommand(request, userId ?? Rep), default);

    private int _requestCounter;

    private async Task<int> GivenReportAsync(params (string Code, decimal Quantity)[] items)
    {
        var result = await Report(Request(
            $"given-{++_requestCounter}",
            items.Select(item => (item.Code, item.Quantity, (string?)"Broken")).ToArray()));
        return result.Value.Id;
    }

    private async Task<List<int>> LineIdsAsync(int id)
        => await _context.MarketBreakageLines.AsNoTracking()
            .Where(line => line.BreakageId == id)
            .OrderBy(line => line.LineNum)
            .Select(line => line.Id)
            .ToListAsync();

    private async Task<MarketBreakageEntity> ReloadAsync(int id)
    {
        await using var fresh = new ApplicationDbContext(_options);
        return await fresh.MarketBreakages.AsNoTracking().Include(b => b.Lines).SingleAsync(b => b.Id == id);
    }

    private Task<ErrorOr.ErrorOr<MarketBreakageDecisionResultDto>> Confirm(
        int id, FakeSap sap, params (int LineId, decimal Quantity)[] lines)
        => Confirm(id, sap, BuildStore(), _context, "RETURNS", lines);

    private Task<ErrorOr.ErrorOr<MarketBreakageDecisionResultDto>> Confirm(
        int id, FakeSap sap, string returnsWarehouse, params (int LineId, decimal Quantity)[] lines)
        => Confirm(id, sap, BuildStore(), _context, returnsWarehouse, lines);

    private Task<ErrorOr.ErrorOr<MarketBreakageDecisionResultDto>> Confirm(
        int id, FakeSap sap, IIdempotencyRequestStore store, ApplicationDbContext context,
        string returnsWarehouse, params (int LineId, decimal Quantity)[] lines)
        => Confirm(id, sap, store, context, returnsWarehouse, StockValidationResult.Success(), lines);

    private static Task<ErrorOr.ErrorOr<MarketBreakageDecisionResultDto>> Confirm(
        int id, FakeSap sap, IIdempotencyRequestStore store, ApplicationDbContext context,
        string returnsWarehouse, StockValidationResult stock, params (int LineId, decimal Quantity)[] lines)
    {
        var handler = new ConfirmMarketBreakageHandler(
            context,
            sap.AsClient(),
            StubProxy.For<IStockValidationService>((method, _) => method.Name switch
            {
                nameof(IStockValidationService.ValidateInventoryTransferStockAsync) => (object)Task.FromResult(stock),
                _ => throw new InvalidOperationException($"Unexpected stock-validation call: {method.Name}")
            }),
            store,
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            Options.Create(new SAPSettings { Enabled = true }),
            Options.Create(new MarketBreakageSettings { ReturnsWarehouseCode = returnsWarehouse }),
            NullLogger<ConfirmMarketBreakageHandler>.Instance);

        return handler.Handle(
            new ConfirmMarketBreakageCommand(
                id,
                lines.Select(line => new ConfirmMarketBreakageLineDto { LineId = line.LineId, ConfirmedQuantity = line.Quantity }).ToList(),
                "Counted at the depot",
                Office),
            default);
    }

    private Task<ErrorOr.ErrorOr<MarketBreakageDecisionResultDto>> Reject(int id, string remarks)
        => new RejectMarketBreakageHandler(
                _context,
                StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
                NullLogger<RejectMarketBreakageHandler>.Instance)
            .Handle(new RejectMarketBreakageCommand(id, remarks, Office), default);

    private IIdempotencyRequestStore BuildStore()
        => new IdempotencyRequestStore(new SingleContextScopeFactory(_options), Options.Create(new SecuritySettings()));

    private sealed class FakeSap
    {
        public const int DocEntry = 7001;

        public List<CreateInventoryTransferRequest> Requests { get; } = [];
        public bool Hold { get; init; }
        public Exception? Failure { get; init; }
        public TaskCompletionSource EnteredSap { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.CreateInventoryTransferAsync) =>
                    Create((CreateInventoryTransferRequest)args![0]!),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private async Task<InventoryTransfer> Create(CreateInventoryTransferRequest request)
        {
            EnteredSap.TrySetResult();
            if (Hold) await Release.Task;
            if (Failure is not null) throw Failure;

            Requests.Add(request);
            return new InventoryTransfer
            {
                DocEntry = DocEntry,
                DocNum = 500 + Requests.Count,
                FromWarehouse = request.FromWarehouse,
                ToWarehouse = request.ToWarehouse
            };
        }
    }

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
