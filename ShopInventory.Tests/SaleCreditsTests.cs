using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins what the van handset and the till are told has been credited against an invoice or a sale.
///
/// Neither app could tell that anything had been given back: the history rows carried no credit, so a
/// returned invoice showed in full and every daily total counted money the shop had refunded. A credit
/// lives in SAP's memo projection, in <c>DesktopCreditNotes</c>, or in both once SAP has taken a till
/// credit — so these tests are about counting each credit exactly once, and only the ones that give
/// something back.
/// </summary>
public sealed class SaleCreditsTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 9, 25);
    private static readonly DateTime DuringDayUtc = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public SaleCreditsTests()
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

    [Fact]
    public async Task An_invoice_credited_in_SAP_carries_the_memo_total_and_number()
    {
        await AddMemo(docEntry: 900, docNum: 55294, (baseEntry: 2001, lineTotal: 20m, vat: 3.10m), (2001, 10m, 1.55m));

        var credits = await SaleCredits.ForInvoicesAsync(_context, [2001, 2002], CancellationToken.None);

        var credit = Assert.Single(credits);
        Assert.Equal(2001, credit.Key);
        Assert.Equal(34.65m, credit.Value.Amount);
        Assert.Equal(["55294"], credit.Value.Numbers);
    }

    [Fact]
    public async Task A_memo_across_two_invoices_credits_each_only_with_its_own_lines()
    {
        await AddMemo(docEntry: 900, docNum: 55294, (baseEntry: 2001, lineTotal: 20m, vat: 0m), (2002, 5m, 0m));

        var credits = await SaleCredits.ForInvoicesAsync(_context, [2001, 2002], CancellationToken.None);

        Assert.Equal(20m, credits[2001].Amount);
        Assert.Equal(5m, credits[2002].Amount);
    }

    [Fact]
    public async Task A_cancelled_memo_gives_nothing_back()
    {
        await AddMemo(docEntry: 900, docNum: 55294, isCancelled: true, (baseEntry: 2001, lineTotal: 20m, vat: 0m));

        var credits = await SaleCredits.ForInvoicesAsync(_context, [2001], CancellationToken.None);

        Assert.Empty(credits);
    }

    [Fact]
    public async Task A_till_credit_SAP_has_not_taken_still_credits_the_invoice()
    {
        var sale = await AddSale("VAN-1", sapDocEntry: 2001);
        await AddTillCredit(sale, 12.50m, DesktopCreditStatuses.Fiscalised, sapDocEntry: null);

        var credits = await SaleCredits.ForInvoicesAsync(_context, [2001], CancellationToken.None);

        Assert.Equal(12.50m, credits[2001].Amount);
        Assert.Equal([$"CN{sale.Id}"], credits[2001].Numbers);
    }

    [Fact]
    public async Task A_till_credit_SAP_has_taken_is_counted_once_not_twice()
    {
        var sale = await AddSale("VAN-1", sapDocEntry: 2001);
        await AddTillCredit(sale, 12.50m, DesktopCreditStatuses.Fiscalised, sapDocEntry: 900);
        await AddMemo(docEntry: 900, docNum: 55294, (baseEntry: 2001, lineTotal: 10.78m, vat: 1.72m));

        var credits = await SaleCredits.ForInvoicesAsync(_context, [2001], CancellationToken.None);

        Assert.Equal(12.50m, credits[2001].Amount);
        Assert.Equal(["55294"], credits[2001].Numbers);
    }

    [Theory]
    [InlineData(DesktopCreditStatuses.Prepared)]
    [InlineData(DesktopCreditStatuses.Submitting)]
    [InlineData(DesktopCreditStatuses.Rejected)]
    [InlineData(DesktopCreditStatuses.ReconciliationRequired)]
    public async Task A_till_credit_ZIMRA_has_not_accepted_gives_nothing_back(string status)
    {
        var sale = await AddSale("VAN-1", sapDocEntry: 2001);
        await AddTillCredit(sale, 12.50m, status, sapDocEntry: null);

        Assert.Empty(await SaleCredits.ForInvoicesAsync(_context, [2001], CancellationToken.None));
        Assert.Empty(await SaleCredits.ForSalesAsync(_context, [(sale.Id, 2001)], CancellationToken.None));
    }

    [Fact]
    public async Task A_credit_on_a_consolidated_sale_credits_the_consolidated_invoice()
    {
        var consolidation = new SaleConsolidationEntity
        {
            CardCode = "KEFGRS",
            ConsolidationDate = Day,
            SapDocEntry = 3001,
            SapDocNum = 780001,
            SaleCount = 1
        };
        _context.SaleConsolidations.Add(consolidation);
        await _context.SaveChangesAsync();

        var sale = await AddSale("TILL-1", sapDocEntry: null, consolidationId: consolidation.Id);
        await AddTillCredit(sale, 8m, DesktopCreditStatuses.Fiscalised, sapDocEntry: null);

        var credits = await SaleCredits.ForInvoicesAsync(_context, [3001], CancellationToken.None);

        Assert.Equal(8m, credits[3001].Amount);
    }

    [Fact]
    public async Task A_sale_carries_its_till_credits_and_memos_against_its_own_invoice()
    {
        var perSale = await AddSale("VAN-1", sapDocEntry: 2001);
        var credited = await AddSale("TILL-1", sapDocEntry: null);
        var untouched = await AddSale("TILL-2", sapDocEntry: null);
        await AddMemo(docEntry: 900, docNum: 55294, (baseEntry: 2001, lineTotal: 30m, vat: 0m));
        await AddTillCredit(credited, 4m, DesktopCreditStatuses.Fiscalised, sapDocEntry: null);
        await AddTillCredit(credited, 6m, DesktopCreditStatuses.Fiscalised, sapDocEntry: null);

        var credits = await SaleCredits.ForSalesAsync(
            _context,
            [(perSale.Id, 2001), (credited.Id, null), (untouched.Id, null)],
            CancellationToken.None);

        Assert.Equal(30m, credits[perSale.Id].Amount);
        Assert.Equal(10m, credits[credited.Id].Amount);
        Assert.Equal([$"CN{credited.Id}", $"CN{credited.Id}-2"], credits[credited.Id].Numbers);
        Assert.False(credits.ContainsKey(untouched.Id));
    }

    [Fact]
    public async Task The_till_sale_list_says_how_much_of_each_sale_was_credited()
    {
        var credited = await AddSale("TILL-1", sapDocEntry: null);
        await AddSale("TILL-2", sapDocEntry: null);
        await AddTillCredit(credited, 40m, DesktopCreditStatuses.Fiscalised, sapDocEntry: null);

        var console = Guid.NewGuid();
        _context.Users.Add(new User
        {
            Id = console,
            Username = "console",
            PasswordHash = "x",
            Role = ApplicationRoles.Cashier,
            IsActive = true,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await new GetDesktopSalesHandler(
                _context,
                new RecordingAuditService(),
                Options.Create(new FiscalisationSettings()),
                Options.Create(new DesktopSalePostingSettings()),
                Options.Create(new VanSalesPostingSettings()))
            .Handle(new GetDesktopSalesQuery(console), CancellationToken.None);

        Assert.False(result.IsError);
        var rows = result.Value.Sales.ToDictionary(sale => sale.ExternalReferenceId);
        Assert.Equal(40m, rows["TILL-1"].CreditedAmount);
        Assert.Equal([$"CN{credited.Id}"], rows["TILL-1"].CreditNoteNumbers);
        Assert.Equal(0m, rows["TILL-2"].CreditedAmount);
        Assert.Empty(rows["TILL-2"].CreditNoteNumbers);

        var wire = System.Text.Json.JsonSerializer.Serialize(
            rows["TILL-1"], new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"creditedAmount\":40", wire);
        Assert.Contains($"\"creditNoteNumbers\":[\"CN{credited.Id}\"]", wire);
    }

    [Fact]
    public void The_credit_travels_under_the_names_the_handset_and_the_till_read()
    {
        // Both apps live in other repositories and read these by name: the handset's history row is
        // snake_case, the till reads the console's camelCase. A rename here fails nothing on this side.
        var van = System.Text.Json.JsonSerializer.Serialize(
            new ShopInventory.DTOs.VanSalesLegacyOrderDto { Credited = 18.5, CreditNotes = "55294" });
        Assert.Contains("\"credited\":18.5", van);
        Assert.Contains("\"credit_notes\":\"55294\"", van);

        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var invoice = System.Text.Json.JsonSerializer.Serialize(
            new ShopInventory.DTOs.InvoiceDto { CreditedAmount = 40m, CreditNoteNumbers = ["CN1753"] }, web);
        Assert.Contains("\"creditedAmount\":40", invoice);
        Assert.Contains("\"creditNoteNumbers\":[\"CN1753\"]", invoice);
    }

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(100, 99.995, true)]
    [InlineData(100, 99.98, false)]
    [InlineData(100, 0, false)]
    public void Covers_means_the_whole_total_to_the_cent(decimal total, decimal credited, bool expected)
    {
        Assert.Equal(expected, new SaleCredit(credited, []).Covers(total));
    }

    // ---- Harness --------------------------------------------------------------------------------

    private async Task<DesktopSaleEntity> AddSale(string reference, int? sapDocEntry, int? consolidationId = null)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosShopTill",
            CardCode = "KEFGRS",
            WarehouseCode = "KEFGRS",
            DocDate = Day,
            TotalAmount = 100m,
            VatAmount = 13.04m,
            AmountPaid = 100m,
            Currency = "USD",
            SapDocEntry = sapDocEntry,
            ConsolidationId = consolidationId,
            CreatedAt = DuringDayUtc,
        };
        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return sale;
    }

    private async Task AddTillCredit(DesktopSaleEntity sale, decimal amount, string status, int? sapDocEntry)
    {
        _context.DesktopCreditNotes.Add(new DesktopCreditNoteEntity
        {
            Id = Guid.NewGuid(),
            SaleId = sale.Id,
            RequestKey = Guid.NewGuid().ToString("N"),
            Number = $"DCN-{Guid.NewGuid():N}",
            OriginalFiscalNumber = sale.ExternalReferenceId,
            Reason = "Damaged",
            Currency = "USD",
            Amount = amount,
            Status = status,
            SapStatus = sapDocEntry is null ? DesktopCreditSapStatuses.Deferred : DesktopCreditSapStatuses.Posted,
            SapDocEntry = sapDocEntry,
            CreatedAtUtc = DuringDayUtc.AddMinutes(_context.DesktopCreditNotes.Count()),
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Task AddMemo(int docEntry, int docNum, params (int baseEntry, decimal lineTotal, decimal vat)[] lines) =>
        AddMemo(docEntry, docNum, isCancelled: false, lines);

    private async Task AddMemo(
        int docEntry, int docNum, bool isCancelled, params (int baseEntry, decimal lineTotal, decimal vat)[] lines)
    {
        _context.SapCreditNoteSnapshots.Add(new SapCreditNoteSnapshotEntity
        {
            SapDocEntry = docEntry,
            SapDocNum = docNum,
            DocDate = Day,
            CardCode = "VAN008",
            DocCurrency = "USD",
            DocTotal = lines.Sum(line => line.lineTotal + line.vat),
            VatSum = lines.Sum(line => line.vat),
            IsCancelled = isCancelled,
            LastSeenInSapAtUtc = DuringDayUtc,
            SyncedAtUtc = DuringDayUtc,
            Lines = lines
                .Select((line, index) => new SapCreditNoteLineSnapshotEntity
                {
                    LineNum = index,
                    ItemCode = "CHS001",
                    BaseType = VanSaleCreditNotes.InvoiceBaseType,
                    BaseEntry = line.baseEntry,
                    LineTotal = line.lineTotal,
                    VatSum = line.vat,
                })
                .ToList()
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
