using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.StockWriteOffs.Commands.CreateStockWriteOff;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Writing stock off a warehouse: what has to be true before the stock is allowed to leave, and what
/// is left behind when it does not.
/// </summary>
/// <remarks>
/// A write-off destroys inventory value and cannot be undone from here, so the interesting cases are
/// all the ones where it must <em>not</em> happen: stock SAP would not confirm, stock SAP could not be
/// asked about, a second submit of the same count, and a post whose outcome nobody knows.
/// </remarks>
public sealed class StockWriteOffTests : IDisposable
{
    private static readonly Guid Clerk = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OtherClerk = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;

    public StockWriteOffTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            NewUser(Clerk, "clerk", "StockController", "Office", "Clerk"),
            NewUser(OtherClerk, "clerk2", "StockController", "Other", "Clerk"));
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_counted_write_off_leaves_SAP_as_a_goods_issue_and_the_record_keeps_its_number()
    {
        var sap = new FakeSap();

        var result = await WriteOff(sap, Request("wo-1", ("BATCHED01", 4m, "B-0099")));

        Assert.False(result.IsError);
        Assert.Equal(StockWriteOffStatuses.Posted, result.Value.WriteOff.Status);
        Assert.Equal(FakeSap.DocNum, result.Value.WriteOff.SapDocNum);

        var stored = await ReloadAsync(result.Value.WriteOff.Id);
        Assert.Equal(StockWriteOffStatuses.Posted, stored.Status);
        Assert.Equal(FakeSap.DocEntry, stored.SapDocEntry);
        Assert.NotNull(stored.PostedAtUtc);
        Assert.Null(stored.LastError);

        var posted = Assert.Single(sap.Requests);
        Assert.Equal("RETURNS", posted.WarehouseCode);
        Assert.Equal("Stock write-off", posted.JournalMemo);

        // The reason is the header's, written onto every line, because SAP keeps it on the line.
        Assert.All(posted.Lines, line => Assert.Equal("Breakage", line.Reason));

        var line = Assert.Single(posted.Lines);
        Assert.Equal("BATCHED01", line.ItemCode);
        Assert.Equal(4m, line.Quantity);
        Assert.Equal("B-0099", Assert.Single(line.BatchNumbers!).BatchNumber);
    }

    [Fact]
    public async Task The_stock_check_is_asked_about_the_warehouse_the_stock_is_leaving_and_no_other()
    {
        var sap = new FakeSap();
        var stock = new RecordingStockValidation(StockValidationResult.Success());

        var result = await WriteOff(sap, Request("wo-2", ("BATCHED01", 4m, "B-0099")), stock: stock);

        Assert.False(result.IsError);

        // A write-off is a transfer with no destination as far as the existing check is concerned,
        // and that is only safe while the check reads the source side alone. If it ever starts
        // reading a destination, this is where it is caught rather than in production.
        var asked = Assert.Single(stock.Requests);
        Assert.Equal("RETURNS", asked.FromWarehouse);
        Assert.Null(asked.ToWarehouse);
        Assert.All(asked.Lines!, line =>
        {
            Assert.Equal("RETURNS", line.FromWarehouseCode);
            Assert.Null(line.ToWarehouseCode);
        });
    }

    [Fact]
    public async Task Stock_SAP_will_not_confirm_is_not_written_off()
    {
        var sap = new FakeSap();
        var short_ = StockValidationResult.Failure([new StockValidationError
        {
            LineNumber = 1,
            ItemCode = "BATCHED01",
            WarehouseCode = "RETURNS",
            BatchNumber = "B-0099",
            RequestedQuantity = 4m,
            AvailableQuantity = 1m
        }]);

        var result = await WriteOff(sap, Request("wo-3", ("BATCHED01", 4m, "B-0099")),
            stock: new RecordingStockValidation(short_));

        Assert.True(result.IsError);
        Assert.Equal("StockWriteOff.InsufficientStock", result.FirstError.Code);
        Assert.Empty(sap.Requests);

        var stored = await ReloadAsync(1);
        Assert.Equal(StockWriteOffStatuses.PostFailed, stored.Status);
        Assert.Contains("Insufficient stock", stored.LastError);
        Assert.Contains("B-0099", stored.LastError);
    }

    [Fact]
    public async Task Stock_SAP_could_not_be_asked_about_is_not_written_off_either()
    {
        var sap = new FakeSap();
        var unread = StockValidationResult.Success();
        unread.UnreadableWarehouses.Add("RETURNS");

        var result = await WriteOff(sap, Request("wo-4", ("BATCHED01", 4m, "B-0099")),
            stock: new RecordingStockValidation(unread));

        // Unread is not the same as short, and the difference decides whether stock is destroyed.
        Assert.True(result.IsError);
        Assert.Equal("StockWriteOff.PostFailed", result.FirstError.Code);
        Assert.Contains("Could not read stock", result.FirstError.Description);
        Assert.Empty(sap.Requests);

        Assert.Equal(StockWriteOffStatuses.PostFailed, (await ReloadAsync(1)).Status);
    }

    [Fact]
    public async Task Resending_the_same_count_answers_with_the_write_off_it_already_posted()
    {
        var sap = new FakeSap();
        var request = Request("wo-5", ("BATCHED01", 4m, "B-0099"));

        var first = await WriteOff(sap, request);
        Assert.False(first.IsError);

        var second = await WriteOff(sap, Request("wo-5", ("BATCHED01", 4m, "B-0099")));

        Assert.False(second.IsError);
        Assert.True(second.Value.AlreadyPosted);
        Assert.Equal(first.Value.WriteOff.Id, second.Value.WriteOff.Id);

        // The stock left once, and only once.
        Assert.Single(sap.Requests);
        Assert.Single(await AllAsync());
    }

    [Fact]
    public async Task A_count_id_another_account_already_used_is_refused_rather_than_adopted()
    {
        var sap = new FakeSap();

        var first = await WriteOff(sap, Request("wo-6", ("BATCHED01", 4m, "B-0099")));
        Assert.False(first.IsError);

        var second = await WriteOff(sap, Request("wo-6", ("BATCHED01", 4m, "B-0099")), userId: OtherClerk);

        Assert.True(second.IsError);
        Assert.Equal("StockWriteOff.DuplicateRequest", second.FirstError.Code);
        Assert.Single(sap.Requests);
    }

    [Fact]
    public async Task A_post_SAP_never_answered_says_so_rather_than_inviting_a_second_one()
    {
        var sap = new FakeSap { Failure = new OperationCanceledException() };

        var result = await WriteOff(sap, Request("wo-7", ("BATCHED01", 4m, "B-0099")));

        Assert.True(result.IsError);

        // The document may exist. A retry would write the stock off twice, so the message says to
        // look in SAP first instead of reading as "that did not work, try again".
        Assert.Contains("not known whether", result.FirstError.Description);
        Assert.Contains("posting again will issue it again", result.FirstError.Description);

        var stored = await ReloadAsync(1);
        Assert.Equal(StockWriteOffStatuses.PostFailed, stored.Status);
        Assert.Contains("not known whether", stored.LastError);
    }

    [Fact]
    public async Task A_refused_post_can_be_posted_again_under_the_same_count_id()
    {
        var refusing = new FakeSap { Failure = new InvalidOperationException("SAP said no") };
        var firstAttempt = await WriteOff(refusing, Request("wo-8", ("BATCHED01", 4m, "B-0099")));
        Assert.True(firstAttempt.IsError);

        var accepting = new FakeSap();
        var retry = await WriteOff(accepting, Request("wo-8", ("BATCHED01", 4m, "B-0099")));

        Assert.False(retry.IsError);
        Assert.Equal(StockWriteOffStatuses.Posted, retry.Value.WriteOff.Status);

        // The retry finished the write-off already raised rather than raising a second one.
        Assert.Single(await AllAsync());
    }

    [Fact]
    public async Task A_reason_nothing_offers_is_refused_before_a_record_is_written()
    {
        var sap = new FakeSap();
        var request = Request("wo-9", ("BATCHED01", 4m, "B-0099"));
        request.Reason = "Because I said so";

        var result = await WriteOff(sap, request);

        Assert.True(result.IsError);
        Assert.Equal("StockWriteOff.UnknownReason", result.FirstError.Code);
        Assert.Empty(sap.Requests);
        Assert.Empty(await AllAsync());
    }

    [Fact]
    public async Task A_reason_is_taken_as_typed_when_neither_SAP_nor_the_settings_name_any()
    {
        // Configuration naming no reasons and SAP defining no reason field is a setup problem, not a
        // reason to refuse every write-off: the words are recorded locally either way.
        var sap = new FakeSap { Reasons = [] };
        var request = Request("wo-12", ("BATCHED01", 4m, "B-0099"));
        request.Reason = "Flood damage";

        var result = await WriteOff(sap, request);

        Assert.False(result.IsError);
        Assert.Equal("Flood damage", Assert.Single(sap.Requests).Lines[0].Reason);
    }

    [Fact]
    public async Task A_warehouse_SAP_does_not_have_is_refused_before_a_record_is_written()
    {
        var sap = new FakeSap();
        var request = Request("wo-10", ("BATCHED01", 4m, "B-0099"));
        request.WarehouseCode = "NOWHERE";

        var result = await WriteOff(sap, request);

        Assert.True(result.IsError);
        Assert.Equal("StockWriteOff.UnknownWarehouse", result.FirstError.Code);
        Assert.Empty(await AllAsync());
    }

    [Fact]
    public async Task Nothing_is_written_off_while_SAP_integration_is_switched_off()
    {
        var sap = new FakeSap();

        var result = await WriteOff(sap, Request("wo-11", ("BATCHED01", 4m, "B-0099")), sapEnabled: false);

        Assert.True(result.IsError);
        Assert.Equal("StockWriteOff.SapDisabled", result.FirstError.Code);
        Assert.Empty(await AllAsync());
    }

    // ---- harness ------------------------------------------------------------------------------

    private static CreateStockWriteOffRequestDto Request(
        string clientRequestId,
        params (string ItemCode, decimal Quantity, string? Batch)[] lines) => new()
        {
            ClientRequestId = clientRequestId,
            WarehouseCode = "RETURNS",
            Reason = "Breakage",
            Remarks = "Counted at the depot",
            Lines = lines
                .Select(line => new CreateStockWriteOffLineDto
                {
                    ItemCode = line.ItemCode,
                    Quantity = line.Quantity,
                    BatchNumber = line.Batch
                })
                .ToList()
        };

    private Task<ErrorOr<StockWriteOffResultDto>> WriteOff(
        FakeSap sap,
        CreateStockWriteOffRequestDto request,
        RecordingStockValidation? stock = null,
        Guid? userId = null,
        bool sapEnabled = true)
    {
        var handler = new CreateStockWriteOffHandler(
            _context,
            sap.AsClient(),
            stock ?? new RecordingStockValidation(StockValidationResult.Success()),
            new IdempotencyRequestStore(new SingleContextScopeFactory(_options), Options.Create(new SecuritySettings())),
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            Options.Create(new SAPSettings { Enabled = sapEnabled }),
            Options.Create(new StockWriteOffSettings()),
            NullLogger<CreateStockWriteOffHandler>.Instance);

        return handler.Handle(new CreateStockWriteOffCommand(request, userId ?? Clerk), default);
    }

    private async Task<StockWriteOffEntity> ReloadAsync(int id)
    {
        await using var fresh = new ApplicationDbContext(_options);
        return await fresh.StockWriteOffs.AsNoTracking().Include(row => row.Lines).SingleAsync(row => row.Id == id);
    }

    private async Task<List<StockWriteOffEntity>> AllAsync()
    {
        await using var fresh = new ApplicationDbContext(_options);
        return await fresh.StockWriteOffs.AsNoTracking().ToListAsync();
    }

    private static User NewUser(Guid id, string username, string role, string? first, string? last) => new()
    {
        Id = id,
        Username = username,
        PasswordHash = "x",
        Role = role,
        FirstName = first,
        LastName = last,
        IsActive = true
    };

    /// <summary>A stock check that answers whatever the test wants and remembers what it was asked.</summary>
    private sealed class RecordingStockValidation(StockValidationResult result) : IStockValidationService
    {
        public List<CreateInventoryTransferRequest> Requests { get; } = [];

        public Task<StockValidationResult> ValidateInventoryTransferStockAsync(
            CreateInventoryTransferRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }

        public Task<StockValidationResult> ValidateInvoiceStockAsync(
            CreateInvoiceRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A write-off does not validate an invoice");

        public List<string> ValidatePositiveQuantities(IEnumerable<QuantityValidationItem> items) => [];

        public Task<bool> HasSufficientBatchQuantityAsync(
            string itemCode, string batchNumber, string warehouseCode, decimal requestedQuantity,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used by a write-off");

        public Task<decimal> GetAvailableQuantityAsync(
            string itemCode, string warehouseCode, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used by a write-off");

        public Task UpdateLocalStockAsync(
            string itemCode, string warehouseCode, decimal quantityChange, string transactionType,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A write-off does not move the local ledger");
    }

    private sealed class FakeSap
    {
        public const int DocEntry = 9001;
        public const int DocNum = 4471;

        public List<CreateGoodsIssueRequest> Requests { get; } = [];
        public Exception? Failure { get; init; }

        /// <summary>What this company database defines on the goods-issue line table.</summary>
        public List<SapDocumentLineReason> Reasons { get; init; } =
            [new SapDocumentLineReason("Breakage", "Broken in the market")];

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.CreateGoodsIssueAsync) =>
                    Create((CreateGoodsIssueRequest)args![0]!),
                nameof(ISAPServiceLayerClient.GetGoodsIssueLineReasonsAsync) =>
                    Task.FromResult<IReadOnlyList<SapDocumentLineReason>>(Reasons),
                nameof(ISAPServiceLayerClient.GetWarehousesAsync) =>
                    Task.FromResult(new List<WarehouseDto>
                    {
                        new() { WarehouseCode = "RETURNS", WarehouseName = "Returns" }
                    }),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<GoodsIssue> Create(CreateGoodsIssueRequest request)
        {
            if (Failure is not null)
            {
                return Task.FromException<GoodsIssue>(Failure);
            }

            Requests.Add(request);
            return Task.FromResult(new GoodsIssue { DocEntry = DocEntry, DocNum = DocNum });
        }
    }

    private sealed class SingleContextScopeFactory(DbContextOptions<ApplicationDbContext> options)
        : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory,
          Microsoft.Extensions.DependencyInjection.IServiceScope,
          IServiceProvider
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
            => serviceType == typeof(ApplicationDbContext) ? new ApplicationDbContext(options) : null;

        public void Dispose()
        {
        }
    }
}
