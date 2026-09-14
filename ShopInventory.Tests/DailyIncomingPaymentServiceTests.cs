using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the rule the business asked for: one incoming payment per business partner per day, posted at
/// 17:00, settling every invoice that customer had posted by then, and no invoice with a payment of its
/// own.
/// </summary>
/// <remarks>
/// SAP has no idempotency for a payment, so most of these are about never sending one twice: a second
/// pass the same day, a lost reply, a refusal, and a day that never posted.
/// </remarks>
public sealed class DailyIncomingPaymentServiceTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 9, 14);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly FakeSap _sap = new();
    private int _nextReference = 1;

    public DailyIncomingPaymentServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = NewContext();
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>CAT is UTC+2 all year, so 17:05 CAT is 15:05 UTC.</summary>
    private static DateTime Cat(DateTime day, int hour, int minute) =>
        DateTime.SpecifyKind(day.Date.AddHours(hour - 2).AddMinutes(minute), DateTimeKind.Utc);

    [Fact]
    public async Task Every_invoice_a_customer_had_posted_by_17_00_goes_on_one_payment()
    {
        await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 0));
        await GivenSaleAsync("BP-1", docEntry: 102, TenderTypes.Ecocash, 10m, postedAt: Cat(Day, 16, 59));
        await GivenConsolidationAsync("BP-1", docEntry: 103, total: 50m, postedAt: Cat(Day, 16, 46));
        await GivenSaleAsync("BP-2", docEntry: 201, TenderTypes.Cash, 7m, postedAt: Cat(Day, 12, 0), source: SaleSourceSystems.Vending);

        var result = await Run(Cat(Day, 17, 0));

        Assert.Equal(2, result.Created);
        Assert.Equal(2, result.Posted);
        Assert.Equal(2, _sap.CreateCalls);

        var bp1 = _sap.Held.Single(held => held.Request.CardCode == "BP-1");
        Assert.Equal([101, 102, 103], bp1.Request.PaymentInvoices!.Select(line => line.DocEntry).Order());
        Assert.Equal(75m, bp1.Request.CashSum);
        Assert.Equal(10m, bp1.Request.TransferSum);
        Assert.Equal("2026-09-14", bp1.Request.DocDate);

        // Every invoice points at the one payment that settled it.
        var sales = await _context.DesktopSales.AsNoTracking().Where(sale => sale.CardCode == "BP-1" && sale.ConsolidationId == null).ToListAsync();
        Assert.All(sales, sale =>
        {
            Assert.Equal(DesktopSalePaymentStatuses.Posted, sale.PaymentStatus);
            Assert.Equal(bp1.Payment.DocNum, sale.PaymentSapDocNum);
        });

        var consolidation = await _context.SaleConsolidations.AsNoTracking().SingleAsync();
        Assert.Equal(DesktopSalePaymentStatuses.Posted, consolidation.PaymentStatus);
        Assert.Equal(bp1.Payment.DocNum, consolidation.PaymentSapDocNum);
    }

    [Fact]
    public async Task A_sale_posted_after_17_00_goes_on_the_next_days_payment()
    {
        var late = await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Cash, 25m, postedAt: Cat(Day, 17, 30));

        var sameEvening = await Run(Cat(Day, 17, 35));
        Assert.Equal(0, sameEvening.Created);
        Assert.Equal(0, _sap.CreateCalls);

        await Run(Cat(Day.AddDays(1), 17, 0));

        var held = Assert.Single(_sap.Held);
        Assert.Equal("2026-09-15", held.Request.DocDate);
        Assert.Equal(101, Assert.Single(held.Request.PaymentInvoices!).DocEntry);
        Assert.Equal(DesktopSalePaymentStatuses.Posted, (await Reload(late)).PaymentStatus);
    }

    [Fact]
    public async Task A_pass_before_17_00_does_not_pay_todays_invoices()
    {
        await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 0));

        var result = await Run(Cat(Day, 16, 55));

        Assert.Equal(0, result.Created);
        Assert.Equal(0, _sap.CreateCalls);
    }

    [Fact]
    public async Task A_van_sale_is_never_paid()
    {
        // Out of scope by the business's decision: van invoices stay as they are.
        await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 0), source: SaleSourceSystems.VanSales);

        await Run(Cat(Day, 17, 0));

        Assert.Equal(0, _sap.CreateCalls);
    }

    [Fact]
    public async Task A_second_pass_the_same_day_never_starts_a_second_payment_for_a_customer()
    {
        await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 0));
        await Run(Cat(Day, 17, 0));

        // Posted before the cut-off, but only committed here after the 17:00 pass had read.
        var straggler = await GivenSaleAsync("BP-1", docEntry: 102, TenderTypes.Cash, 5m, postedAt: Cat(Day, 16, 59));

        var retry = await Run(Cat(Day, 17, 10));

        Assert.Equal(0, retry.Created);
        // Left alone, not refused by the unique index. That index is the backstop, and leaning on it
        // would report a failure for every such customer on every retry all evening.
        Assert.Equal(0, retry.Failed);
        Assert.Equal(1, _sap.CreateCalls);
        Assert.Null((await Reload(straggler)).PaymentStatus);

        await Run(Cat(Day.AddDays(1), 17, 0));

        Assert.Equal(2, _sap.CreateCalls);
        Assert.Equal(102, Assert.Single(_sap.Held.Last().Request.PaymentInvoices!).DocEntry);
    }

    [Fact]
    public async Task A_lost_reply_is_adopted_from_SAP_and_never_sent_twice()
    {
        var sale = await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 0));
        _sap.NextCreate = CreateOutcome.CommitThenLoseReply;

        var first = await Run(Cat(Day, 17, 0));

        Assert.Equal(1, first.Unresolved);
        Assert.Equal(DailyIncomingPaymentStatus.Unresolved, (await _context.DailyIncomingPayments.AsNoTracking().SingleAsync()).Status);

        var second = await Run(Cat(Day, 17, 10));

        Assert.Equal(1, second.Adopted);
        Assert.Equal(1, _sap.CreateCalls);

        var payment = await _context.DailyIncomingPayments.AsNoTracking().SingleAsync();
        Assert.Equal(DailyIncomingPaymentStatus.Posted, payment.Status);
        Assert.Equal(_sap.Held.Single().Payment.DocNum, payment.SapDocNum);
        Assert.Equal(payment.SapDocNum, (await Reload(sale)).PaymentSapDocNum);
    }

    [Fact]
    public async Task An_unanswered_post_SAP_does_not_hold_is_sent_again_only_after_the_grace_window()
    {
        await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 0));
        _sap.NextCreate = CreateOutcome.LoseRequest;

        await Run(Cat(Day, 17, 0));

        // Inside the window a committed payment may simply not be visible yet.
        await Run(Cat(Day, 17, 10));
        Assert.Equal(1, _sap.CreateCalls);

        var afterGrace = await Run(Cat(Day, 17, 20));

        Assert.Equal(2, _sap.CreateCalls);
        Assert.Equal(1, afterGrace.Posted);
        Assert.Single(_sap.Held);
    }

    [Fact]
    public async Task A_refused_payment_keeps_its_invoices_and_is_sent_again()
    {
        var sale = await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 0));
        _sap.NextCreate = CreateOutcome.Refuse;

        var refused = await Run(Cat(Day, 17, 0));

        Assert.Equal(1, refused.Failed);
        var pending = await _context.DailyIncomingPayments.AsNoTracking().Include(p => p.Lines).SingleAsync();
        Assert.Equal(DailyIncomingPaymentStatus.Pending, pending.Status);
        Assert.Null(pending.PostIssuedAtUtc);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(sale.Id, Assert.Single(pending.Lines).DesktopSaleId);

        var retried = await Run(Cat(Day, 17, 10));

        Assert.Equal(1, retried.Posted);
        Assert.Equal(2, _sap.CreateCalls);
        Assert.Single(_sap.Held);
    }

    [Fact]
    public async Task A_day_that_never_posted_hands_its_invoices_to_the_next_days_payment()
    {
        var sale = await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 0));
        _sap.RefuseAll = true;
        await Run(Cat(Day, 17, 0));

        _sap.RefuseAll = false;
        var nextDay = await Run(Cat(Day.AddDays(1), 17, 0));

        Assert.Equal(1, nextDay.Released);
        Assert.Equal(1, nextDay.Posted);

        var rows = await _context.DailyIncomingPayments.AsNoTracking().OrderBy(p => p.PaymentDate).ToListAsync();
        Assert.Equal(DailyIncomingPaymentStatus.Released, rows[0].Status);
        Assert.Equal(DailyIncomingPaymentStatus.Posted, rows[1].Status);

        var held = Assert.Single(_sap.Held);
        Assert.Equal("2026-09-15", held.Request.DocDate);
        Assert.Equal(held.Payment.DocNum, (await Reload(sale)).PaymentSapDocNum);
    }

    [Fact]
    public async Task An_invoice_SAP_shows_settled_is_left_off_and_a_credited_one_pays_only_what_it_owes()
    {
        var settled = await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 0));
        await GivenSaleAsync("BP-1", docEntry: 102, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 5));
        _sap.Invoices[101].PaidToDate = 25m;
        // A 10.00 return was credited against it at the counter.
        _sap.Invoices[102].PaidToDate = 10m;

        await Run(Cat(Day, 17, 0));

        var held = Assert.Single(_sap.Held);
        var line = Assert.Single(held.Request.PaymentInvoices!);
        Assert.Equal(102, line.DocEntry);
        Assert.Equal(15m, line.SumApplied);
        Assert.Equal(15m, held.Request.CashSum);

        Assert.Equal(DesktopSalePaymentStatuses.PostedUnconfirmed, (await Reload(settled)).PaymentStatus);
    }

    [Fact]
    public async Task A_customer_whose_every_invoice_is_settled_gets_no_payment()
    {
        await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 0));
        _sap.Invoices[101].PaidToDate = 25m;

        var result = await Run(Cat(Day, 17, 0));

        Assert.Equal(1, result.NothingToPay);
        Assert.Equal(0, _sap.CreateCalls);
    }

    [Fact]
    public async Task A_swipe_without_a_card_code_waits_and_the_rest_of_the_day_still_pays()
    {
        var swipe = await GivenSaleAsync("BP-1", docEntry: 101, TenderTypes.Swipe, 40m, postedAt: Cat(Day, 9, 0));
        await GivenSaleAsync("BP-1", docEntry: 102, TenderTypes.Cash, 25m, postedAt: Cat(Day, 9, 5));

        await Run(Cat(Day, 17, 0));

        var held = Assert.Single(_sap.Held);
        Assert.Equal(102, Assert.Single(held.Request.PaymentInvoices!).DocEntry);
        Assert.Equal(0m, held.Request.CreditSum);

        var waiting = await Reload(swipe);
        Assert.Equal(DesktopSalePaymentStatuses.Unmapped, waiting.PaymentStatus);
        Assert.Contains("SwipeCreditCardCode", waiting.LastPaymentError);
    }

    [Theory]
    // 17:00 CAT exactly is today's cut-off.
    [InlineData(15, 0, "2026-09-14", 15)]
    [InlineData(21, 59, "2026-09-14", 15)]
    // A minute before it, the most recent cut-off passed is yesterday's.
    [InlineData(14, 59, "2026-09-13", 15)]
    // 01:00 CAT on the 15th is 23:00 UTC on the 14th: still the 14th's cut-off.
    [InlineData(23, 0, "2026-09-14", 15)]
    public void The_period_is_the_most_recent_17_00_CAT_that_has_passed(
        int utcHour, int utcMinute, string expectedDate, int expectedCutoffUtcHour)
    {
        var (paymentDate, cutoffUtc) = DailyIncomingPaymentService.PeriodFor(
            DateTime.SpecifyKind(Day.AddHours(utcHour).AddMinutes(utcMinute), DateTimeKind.Utc),
            new TimeSpan(17, 0, 0),
            QuartzConfiguration.CatTimeZone);

        Assert.Equal(DateTime.Parse(expectedDate), paymentDate);
        Assert.Equal(expectedCutoffUtcHour, cutoffUtc.Hour);
        Assert.Equal(paymentDate, cutoffUtc.Date);
    }

    // ---- Harness ------------------------------------------------------------------------------------

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    /// <summary>Each pass gets its own context and service, the way each Quartz firing gets a scope.</summary>
    private async Task<DailyIncomingPaymentRunResult> Run(DateTime utcNow)
    {
        await using var context = NewContext();
        var service = new DailyIncomingPaymentService(
            context,
            _sap.Client,
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            Options.Create(new DesktopSalePostingSettings()),
            Options.Create(new SAPSettings()),
            NullLogger<DailyIncomingPaymentService>.Instance);

        return await service.RunAsync(utcNow);
    }

    private async Task<DesktopSaleEntity> Reload(DesktopSaleEntity sale) =>
        await _context.DesktopSales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);

    private async Task<DesktopSaleEntity> GivenSaleAsync(
        string cardCode,
        int docEntry,
        string tender,
        decimal amount,
        DateTime postedAt,
        string source = SaleSourceSystems.ShopTill,
        int? consolidationId = null)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = $"KEFSHOP-01-20260914-{_nextReference++:000000}",
            SourceSystem = source,
            CardCode = cardCode,
            DocDate = postedAt.Date,
            TotalAmount = amount,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            ConsolidationId = consolidationId,
            WarehouseCode = "KEFSHOP",
            PaymentMethod = tender,
            AmountPaid = amount,
            SapDocEntry = consolidationId is null ? docEntry : null,
            SapDocNum = consolidationId is null ? docEntry : null,
            PostedAt = postedAt
        };

        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();

        if (consolidationId is null)
        {
            _sap.Invoices[docEntry] = new Invoice { DocEntry = docEntry, DocNum = docEntry, DocTotal = amount, DocCurrency = "USD" };
        }

        return sale;
    }

    private async Task GivenConsolidationAsync(string cardCode, int docEntry, decimal total, DateTime postedAt)
    {
        var consolidation = new SaleConsolidationEntity
        {
            CardCode = cardCode,
            ConsolidationDate = postedAt.Date,
            SapDocEntry = docEntry,
            SapDocNum = docEntry,
            PostedAt = postedAt,
            Status = ConsolidationStatus.Posted,
            TotalAmount = total,
            SaleCount = 1
        };

        _context.SaleConsolidations.Add(consolidation);
        await _context.SaveChangesAsync();

        await GivenSaleAsync(cardCode, docEntry, TenderTypes.Cash, total, postedAt,
            source: SaleSourceSystems.LegacyDesktop, consolidationId: consolidation.Id);

        _sap.Invoices[docEntry] = new Invoice { DocEntry = docEntry, DocNum = docEntry, DocTotal = total, DocCurrency = "USD" };
    }

    private enum CreateOutcome
    {
        Succeed,

        /// <summary>SAP commits the payment and the reply never arrives.</summary>
        CommitThenLoseReply,

        /// <summary>The request never reaches SAP, and the caller cannot tell.</summary>
        LoseRequest,

        /// <summary>SAP answers with a refusal.</summary>
        Refuse
    }

    private sealed class FakeSap
    {
        public Dictionary<int, Invoice> Invoices { get; } = new();
        public List<(CreateIncomingPaymentRequest Request, IncomingPayment Payment)> Held { get; } = [];
        public CreateOutcome NextCreate { get; set; } = CreateOutcome.Succeed;
        public bool RefuseAll { get; set; }
        public int CreateCalls { get; private set; }

        private int _nextDocNum = 9000;

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetIncomingPaymentsByCustomerAsync) when args!.Length == 4 =>
                (object)Task.FromResult(Held
                    .Where(held => held.Payment.CardCode == (string)args[0]!
                                   && held.Payment.DocDate == ((DateTime)args[1]!).ToString("yyyy-MM-dd"))
                    .Select(held => held.Payment)
                    .ToList()),

            nameof(ISAPServiceLayerClient.GetInvoiceBalancesByDocEntriesAsync) =>
                Task.FromResult(((IEnumerable<int>)args![0]!)
                    .Where(Invoices.ContainsKey)
                    .Select(docEntry => Invoices[docEntry])
                    .ToList()),

            nameof(ISAPServiceLayerClient.CreateIncomingPaymentAsync) => Create((CreateIncomingPaymentRequest)args![0]!),

            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        private Task<IncomingPayment> Create(CreateIncomingPaymentRequest request)
        {
            CreateCalls++;
            var outcome = RefuseAll ? CreateOutcome.Refuse : NextCreate;
            NextCreate = CreateOutcome.Succeed;

            if (outcome == CreateOutcome.Refuse)
            {
                throw new SapRequestRejectedException("create the incoming payment", HttpStatusCode.BadRequest, "Refused.");
            }

            if (outcome == CreateOutcome.LoseRequest)
            {
                throw new TimeoutException("The request timed out.");
            }

            var docNum = _nextDocNum++;
            var payment = new IncomingPayment
            {
                DocEntry = docNum,
                DocNum = docNum,
                CardCode = request.CardCode,
                DocDate = request.DocDate,
                Remarks = request.Remarks,
                Cancelled = "tNO"
            };
            Held.Add((request, payment));

            foreach (var line in request.PaymentInvoices!)
            {
                Invoices[line.DocEntry].PaidToDate += line.SumApplied;
            }

            if (outcome == CreateOutcome.CommitThenLoseReply)
            {
                throw new TimeoutException("The reply was lost.");
            }

            return Task.FromResult(payment);
        }
    }
}
