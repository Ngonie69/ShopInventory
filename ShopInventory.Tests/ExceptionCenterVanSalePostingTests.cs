using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.ExceptionCenter;
using ShopInventory.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;
using ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The exception center's view of van sales that have not reached SAP.
/// </summary>
/// <remarks>
/// This source carries a kind of stuck work none of the others do. Everywhere else a stuck item is a row
/// that tried and failed, and says so. A van sale can instead be stuck because nothing ever looked at it:
/// it carries the trading day the handset sold it on, the posting job reaches back a bounded number of
/// days, and once a sale is behind that window no run selects it again — no error written, no attempt
/// recorded, and a customer holding a fiscal receipt for takings SAP has never heard of.
///
/// So the load-bearing tests here are the ones separating the three populations: a sale still waiting for
/// the next pass is not an exception, a stranded one is, and it has to reach a human rather than sit
/// under a label saying it is retrying.
/// </remarks>
public sealed class ExceptionCenterVanSalePostingTests : IDisposable
{
    private static readonly DateTime TradingDate = new(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 8, 18, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>Seven days back from the trading day, which is the production default.</summary>
    private static readonly DateTime WindowStart = TradingDate.AddDays(-7);

    private const string Blocked = "Blocked";
    private const string Retrying = "Retrying";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingSapClient _sap = new();

    public ExceptionCenterVanSalePostingTests()
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

    // ── What the source shows, and what it leaves alone ─────────────────────

    /// <summary>
    /// The reason this source exists. Nothing failed and nothing was attempted — the sale is simply
    /// older than any run will ask for, and before this it appeared nowhere at all.
    /// </summary>
    [Fact]
    public async Task A_sale_past_the_posting_window_is_listed_even_though_it_never_failed()
    {
        var stranded = AddSale("VAN006-INV-20260808-AAA111", daysBack: 10);
        Assert.Equal(0, stranded.PostingAttempts);
        Assert.Null(stranded.LastPostingError);
        await _context.SaveChangesAsync();

        var item = Assert.Single(await LoadAsync());

        Assert.Equal(ExceptionCenterSources.VanSalePosting, item.Source);
        Assert.Equal("VAN006-INV-20260808-AAA111", item.Reference);
        Assert.Equal("Stranded", item.Status);

        // It has no error of its own, so the row says why it is here instead of showing a blank cell.
        Assert.Contains("outside the posting window", item.LastError);
    }

    /// <summary>
    /// And it has to read as a human's problem. Zero attempts against a budget would otherwise look like
    /// a sale that has barely started retrying, which is the opposite of the truth.
    /// </summary>
    [Fact]
    public async Task A_stranded_sale_is_triaged_as_needing_a_human()
    {
        AddSale("VAN006-INV-20260808-AAA111", daysBack: 10);
        await _context.SaveChangesAsync();

        var item = Assert.Single(await LoadAsync());
        GetExceptionCenterHandler.Enrich(item, Now);

        Assert.Equal(0, item.MaxRetries);
        Assert.Equal(Blocked, item.Triage);
    }

    /// <summary>
    /// The ordinary failure: SAP refused it and a pass will offer it again. Worth listing, since the
    /// money is not in SAP, but it is not waiting on anybody yet.
    /// </summary>
    [Fact]
    public async Task A_sale_sap_refused_is_listed_and_still_counts_as_retrying()
    {
        var failed = AddSale("VAN006-INV-20260817-BBB222", daysBack: 1);
        failed.PostingAttempts = 2;
        failed.LastPostingError = "SAP rejected the invoice: item is blocked for sale.";
        await _context.SaveChangesAsync();

        var item = Assert.Single(await LoadAsync());
        GetExceptionCenterHandler.Enrich(item, Now);

        Assert.Equal("Failed", item.Status);
        Assert.Equal(2, item.RetryCount);
        Assert.Equal(VanSalesEndOfDayPostingService.MaxPostingAttempts, item.MaxRetries);
        Assert.Equal(Retrying, item.Triage);
    }

    [Fact]
    public async Task A_sale_that_has_used_up_its_attempts_needs_a_human()
    {
        var exhausted = AddSale("VAN006-INV-20260817-BBB222", daysBack: 1);
        exhausted.PostingAttempts = VanSalesEndOfDayPostingService.MaxPostingAttempts;
        exhausted.LastPostingError = "SAP rejected the invoice: the period is closed.";
        await _context.SaveChangesAsync();

        var item = Assert.Single(await LoadAsync());
        GetExceptionCenterHandler.Enrich(item, Now);

        Assert.Equal(Blocked, item.Triage);
    }

    /// <summary>
    /// The one that must stay out. Holding sales and posting them on a timer is how this route works, so
    /// listing a sale that is merely waiting would report normal trading as a fault: every van sale of
    /// the day would appear, and the source would be worthless inside a week.
    /// </summary>
    [Fact]
    public async Task A_sale_merely_waiting_for_the_next_pass_is_not_an_exception()
    {
        AddSale("VAN006-INV-20260818-CCC333", daysBack: 0);
        AddSale("VAN006-INV-20260816-DDD444", daysBack: 2);
        await _context.SaveChangesAsync();

        Assert.Empty(await LoadAsync());
    }

    [Fact]
    public async Task Sales_already_in_sap_are_not_listed()
    {
        var posted = AddSale("VAN006-INV-20260808-AAA111", daysBack: 10);
        posted.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
        posted.SapDocNum = 9001;
        await _context.SaveChangesAsync();

        Assert.Empty(await LoadAsync());
    }

    /// <summary>
    /// Till and vending sales share this table but reach SAP by their own route, with their own window
    /// and their own attempt budget. Claiming them here would list them against two owners.
    /// </summary>
    [Fact]
    public async Task Till_sales_belong_to_the_other_route_and_are_not_listed()
    {
        var till = AddSale("DESKTOP-1", daysBack: 10);
        till.SourceSystem = "KefalosDesktop";
        await _context.SaveChangesAsync();

        Assert.Empty(await LoadAsync());
    }

    /// <summary>
    /// The dashboard takes its per-source total with a separate count. Were it to disagree with the rows
    /// beneath it, it would either hide work or claim work that is not there.
    /// </summary>
    [Fact]
    public async Task The_headline_count_matches_the_rows_it_summarises()
    {
        AddSale("VAN006-INV-20260808-AAA111", daysBack: 10);
        var failed = AddSale("VAN006-INV-20260817-BBB222", daysBack: 1);
        failed.LastPostingError = "SAP timed out.";
        AddSale("VAN006-INV-20260818-CCC333", daysBack: 0);
        await _context.SaveChangesAsync();

        var listed = await LoadAsync();
        var counted = await _context.DesktopSales.CountAsync(
            GetExceptionCenterHandler.VanSalePostingPredicate(WindowStart));

        Assert.Equal(2, listed.Count);
        Assert.Equal(listed.Count, counted);
    }

    /// <summary>
    /// The takings a stranded sale represents are the point of surfacing it, so the row has to carry
    /// them — the dashboard totals money held up across every source.
    /// </summary>
    [Fact]
    public async Task A_listed_sale_carries_the_money_it_is_holding_up()
    {
        AddSale("VAN006-INV-20260808-AAA111", daysBack: 10);
        await _context.SaveChangesAsync();

        var item = Assert.Single(await LoadAsync());

        Assert.Equal(100m, item.Amount);
        Assert.Equal("USD", item.Currency);
        Assert.Equal("VAN006", item.Location);
        Assert.Contains("sold 2026-08-08", item.SourceSystem);
    }

    // ── Acknowledging and assigning ─────────────────────────────────────────

    /// <summary>
    /// Acknowledge and assign both refuse to write state for an item they cannot find, so a source that
    /// the lookup does not know is one whose rows can be listed but never triaged.
    /// </summary>
    [Fact]
    public async Task A_listed_sale_can_be_found_by_the_lookup_acknowledge_and_assign_use()
    {
        var stranded = AddSale("VAN006-INV-20260808-AAA111", daysBack: 10);
        await _context.SaveChangesAsync();

        Assert.True(await ExceptionCenterItemLookup.ExistsAsync(
            _context, ExceptionCenterSources.VanSalePosting, stranded.Id.ToString(), default));

        Assert.False(await ExceptionCenterItemLookup.ExistsAsync(
            _context, ExceptionCenterSources.VanSalePosting, "999999", default));
    }

    // ── Retry ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The retry that matters, and the reason it posts rather than requeues. The sales most likely to be
    /// in front of somebody pressing this button are the stranded ones — resetting an attempt count and
    /// waiting would leave them exactly where they were, because no run will ask for their day again.
    /// </summary>
    [Fact]
    public async Task Retrying_a_stranded_sale_posts_it_to_sap()
    {
        var stranded = AddSale("VAN006-INV-20260808-AAA111", daysBack: 10);
        await _context.SaveChangesAsync();

        var result = await Retry(stranded.Id);

        Assert.False(result.IsError);

        var created = Assert.Single(_sap.Created);
        Assert.Equal("VAN006-INV-20260808-AAA111", created.U_Van_saleorder);

        // Booked against the day it was sold, not the day somebody noticed.
        Assert.Equal("2026-08-08", created.DocDate);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, sale.ConsolidationStatus);
        Assert.NotNull(sale.SapDocNum);

        // And it drops off the list, which is the only signal anybody gets that it is dealt with.
        Assert.Empty(await LoadAsync());
    }

    /// <summary>
    /// A sale that has burned its automatic attempts is precisely what a human is being asked to look
    /// at, so the cap must not also block the retry they were pointed at.
    /// </summary>
    [Fact]
    public async Task Retrying_a_sale_that_exhausted_its_attempts_still_posts()
    {
        var exhausted = AddSale("VAN006-INV-20260817-BBB222", daysBack: 1);
        exhausted.PostingAttempts = VanSalesEndOfDayPostingService.MaxPostingAttempts;
        exhausted.LastPostingError = "SAP rejected the invoice: the period is closed.";
        await _context.SaveChangesAsync();

        var result = await Retry(exhausted.Id);

        Assert.False(result.IsError);
        Assert.Single(_sap.Created);
    }

    /// <summary>
    /// A retry SAP refuses has to come back as a failure. Reporting success would take the sale off
    /// somebody's list while it is still Pending and still not in SAP — the same silence this whole
    /// source exists to end.
    /// </summary>
    [Fact]
    public async Task A_retry_sap_refuses_reports_the_failure_and_leaves_the_sale_pending()
    {
        var stranded = AddSale("VAN006-INV-20260808-AAA111", daysBack: 10);
        await _context.SaveChangesAsync();

        _sap.FailFor.Add("VAN006-INV-20260808-AAA111");

        var result = await Retry(stranded.Id);

        Assert.True(result.IsError);
        Assert.Contains("blocked for sale", result.FirstError.Description);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleConsolidationStatus.Pending, sale.ConsolidationStatus);
        Assert.Equal(1, sale.PostingAttempts);
        Assert.NotNull(sale.LastPostingError);

        // Still listed, so it is still somebody's problem.
        Assert.Single(await LoadAsync());
    }

    /// <summary>
    /// A sale SAP already holds is adopted rather than posted a second time. The customer is holding one
    /// fiscal receipt, and a duplicate invoice can only be undone with a manual credit note.
    /// </summary>
    [Fact]
    public async Task A_retry_of_a_sale_sap_already_holds_adopts_it()
    {
        var stranded = AddSale("VAN006-INV-20260808-AAA111", daysBack: 10);
        await _context.SaveChangesAsync();

        _sap.ExistingByVanSaleOrder["VAN006-INV-20260808-AAA111"] = new Invoice { DocEntry = 77, DocNum = 9001 };

        var result = await Retry(stranded.Id);

        Assert.False(result.IsError);
        Assert.Empty(_sap.Created);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(9001, sale.SapDocNum);
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, sale.ConsolidationStatus);
    }

    [Fact]
    public async Task Retrying_a_sale_that_is_not_there_is_reported_as_not_found()
    {
        var result = await Retry(999999);

        Assert.True(result.IsError);
        Assert.Empty(_sap.Created);
    }

    /// <summary>
    /// A sale already in SAP has nothing to retry, and re-posting one would be the duplicate invoice
    /// this route spends most of its design avoiding.
    /// </summary>
    [Fact]
    public async Task Retrying_a_sale_that_is_already_in_sap_does_nothing()
    {
        var posted = AddSale("VAN006-INV-20260808-AAA111", daysBack: 10);
        posted.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
        posted.SapDocNum = 9001;
        await _context.SaveChangesAsync();

        var result = await Retry(posted.Id);

        Assert.True(result.IsError);
        Assert.Empty(_sap.Created);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private Task<List<ExceptionCenterItemDto>> LoadAsync()
        => GetExceptionCenterHandler.LoadVanSalePostingFailuresAsync(_context, WindowStart, 750, default);

    private async Task<ErrorOr.ErrorOr<ErrorOr.Success>> Retry(int saleId)
    {
        var handler = new RetryExceptionCenterItemHandler(
            _context,
            StubProxy.Unused<IInvoiceQueueService>(),
            StubProxy.Unused<IInventoryTransferQueueService>(),
            new VanSalesEndOfDayPostingService(
                _context,
                _sap.Client,
                Options.Create(new VanSalesPostingSettings()),
                NullLogger<VanSalesEndOfDayPostingService>.Instance),
            StubProxy.Unused<MediatR.IMediator>(),
            new HttpContextAccessor(),
            NullLogger<RetryExceptionCenterItemHandler>.Instance);

        return await handler.Handle(
            new RetryExceptionCenterItemCommand(
                ExceptionCenterSources.VanSalePosting, saleId.ToString()),
            default);
    }

    private DesktopSaleEntity AddSale(string reference, int daysBack)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "SIM001",
            DocDate = TradingDate.AddDays(-daysBack).Date,
            NumAtCard = reference,
            TotalAmount = 100m,
            VatAmount = 13.04m,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ReceiptGlobalNo = 500 + daysBack,
            ReceiptCounter = 1,
            FiscalDayNo = "19",
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            WarehouseCode = "VAN006",
            CostCentreCode = "CC006",
            AmountPaid = 100m,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    Quantity = 2m,
                    UnitPrice = 50m,
                    LineTotal = 100m,
                    WarehouseCode = "VAN006"
                }
            ]
        };

        _context.DesktopSales.Add(sale);
        return sale;
    }

    /// <summary>
    /// Records what would have gone to SAP. Built on <see cref="StubProxy"/> because
    /// <see cref="ISAPServiceLayerClient"/> has well over a hundred members; anything the posting service
    /// calls beyond the two answered here throws, so an unexpected SAP call fails the test loudly.
    /// </summary>
    private sealed class RecordingSapClient
    {
        public List<CreateInvoiceRequest> Created { get; } = [];
        public Dictionary<string, Invoice> ExistingByVanSaleOrder { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        private int _nextDocNum = 1000;

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) =>
                (object)Task.FromResult<Invoice?>(
                    ExistingByVanSaleOrder.TryGetValue((string)args![0]!, out var invoice) ? invoice : null),

            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => CreateInvoice((CreateInvoiceRequest)args![0]!),

            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        private Task<Invoice> CreateInvoice(CreateInvoiceRequest request)
        {
            if (FailFor.Contains(request.U_Van_saleorder ?? string.Empty))
            {
                throw new InvalidOperationException("SAP rejected the invoice: item is blocked for sale.");
            }

            Created.Add(request);
            var docNum = _nextDocNum++;
            return Task.FromResult(new Invoice { DocEntry = docNum, DocNum = docNum });
        }
    }
}
