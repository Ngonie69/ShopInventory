using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
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

    // ---- Which lines were credited ----------------------------------------------------------------

    private static readonly List<(int LineNum, string? ItemCode, decimal Quantity, decimal LineTotal)> TwoLineInvoice =
    [
        (0, "MOZ-1KG", 10m, 100m),
        (1, "FET-500", 4m, 20m),
    ];

    [Fact]
    public async Task A_memo_line_lands_on_the_invoice_line_it_is_based_on_with_its_quantity_worked_out()
    {
        // The memo projection stores no quantity. Line 0 sells at 10.00 net, so 20.00 net is two.
        await AddMemo(docEntry: 900, docNum: 55294, (baseEntry: 2001, lineTotal: 20m, vat: 3.10m));
        await SetBaseLine(docEntry: 900, baseLine: 0);

        var credit = (await SaleCredits.ForInvoicesAsync(_context, [2001], CancellationToken.None))[2001];
        var byLine = SaleCredits.ByInvoiceLine(credit, TwoLineInvoice);

        var line = Assert.Single(byLine);
        Assert.Equal(0, line.Key);
        Assert.Equal(2m, line.Value.Quantity);
        Assert.Equal(23.10m, line.Value.Amount);
    }

    [Fact]
    public async Task A_till_credit_SAP_has_not_taken_lands_on_its_invoice_line_with_its_own_quantity()
    {
        // Receipt order is the order the lines were taken (by Id); invoice order is by LineNum. The
        // feta was taken first but is line 1 on the invoice, so receipt line 1 is invoice line 1.
        var sale = await AddSale("VAN-1", sapDocEntry: 2001, consolidationId: null, (1, "FET-500", 4m), (0, "MOZ-1KG", 10m));
        await AddTillCredit(sale, 11.55m, DesktopCreditStatuses.Fiscalised, sapDocEntry: null,
            Plan((ReceiptLine: 1, Name: "FET-500", Quantity: 1m, UnitPrice: 11.55m)));

        var credit = (await SaleCredits.ForInvoicesAsync(_context, [2001], CancellationToken.None))[2001];
        var byLine = SaleCredits.ByInvoiceLine(credit, TwoLineInvoice);

        var line = Assert.Single(byLine);
        Assert.Equal(1, line.Key);
        Assert.Equal(1m, line.Value.Quantity);
        Assert.Equal(11.55m, line.Value.Amount);
    }

    [Fact]
    public async Task A_consolidated_sale_credit_lands_on_the_invoice_line_carrying_its_item()
    {
        var consolidation = new SaleConsolidationEntity
        {
            CardCode = "KEFGRS", ConsolidationDate = Day, SapDocEntry = 2001, SapDocNum = 780001, SaleCount = 1
        };
        _context.SaleConsolidations.Add(consolidation);
        await _context.SaveChangesAsync();

        var sale = await AddSale("TILL-1", sapDocEntry: null, consolidation.Id, (0, "FET-500", 2m));
        await AddTillCredit(sale, 5m, DesktopCreditStatuses.Fiscalised, sapDocEntry: null,
            Plan((ReceiptLine: 1, Name: "FET-500", Quantity: 1m, UnitPrice: 5m)));

        var credit = (await SaleCredits.ForInvoicesAsync(_context, [2001], CancellationToken.None))[2001];

        Assert.Equal(new InvoiceLineCredit(1m, 5m), SaleCredits.ByInvoiceLine(credit, TwoLineInvoice)[1]);
    }

    [Fact]
    public async Task Two_credits_on_one_line_add_up()
    {
        await AddMemo(docEntry: 900, docNum: 55294, (baseEntry: 2001, lineTotal: 10m, vat: 0m));
        await SetBaseLine(docEntry: 900, baseLine: 0);
        await AddMemo(docEntry: 901, docNum: 55295, (baseEntry: 2001, lineTotal: 30m, vat: 0m));
        await SetBaseLine(docEntry: 901, baseLine: 0);

        var credit = (await SaleCredits.ForInvoicesAsync(_context, [2001], CancellationToken.None))[2001];

        Assert.Equal(new InvoiceLineCredit(4m, 40m), SaleCredits.ByInvoiceLine(credit, TwoLineInvoice)[0]);
    }

    [Fact]
    public void The_till_invoice_says_what_was_credited_on_each_line_and_zero_on_the_rest()
    {
        var invoice = new ShopInventory.DTOs.InvoiceDto
        {
            DocEntry = 2001,
            Lines =
            [
                new ShopInventory.DTOs.InvoiceLineDto { LineNum = 0, ItemCode = "MOZ-1KG", Quantity = 10m, LineTotal = 100m },
                new ShopInventory.DTOs.InvoiceLineDto { LineNum = 1, ItemCode = "FET-500", Quantity = 4m, LineTotal = 20m },
            ]
        };

        SaleCredits.ApplyTo(invoice, new SaleCredit(11.55m, ["CN1"]) { Lines = [new CreditedLine(1, "FET-500", 2m, 11.55m, null)] });

        Assert.Equal(11.55m, invoice.CreditedAmount);
        Assert.Equal(0m, invoice.Lines[0].CreditedQuantity);
        Assert.Equal(0m, invoice.Lines[0].CreditedAmount);
        Assert.Equal(2m, invoice.Lines[1].CreditedQuantity);
        Assert.Equal(11.55m, invoice.Lines[1].CreditedAmount);
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

    private async Task<DesktopSaleEntity> AddSale(
        string reference, int? sapDocEntry, int? consolidationId = null, params (int LineNum, string Item, decimal Quantity)[] lines)
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
            Lines = lines
                .Select(line => new DesktopSaleLineEntity
                {
                    LineNum = line.LineNum,
                    ItemCode = line.Item,
                    ItemDescription = line.Item,
                    Quantity = line.Quantity,
                    UnitPrice = 10m,
                    LineTotal = 10m * line.Quantity,
                    WarehouseCode = "KEFGRS",
                })
                .ToList(),
        };
        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return sale;
    }

    private async Task AddTillCredit(
        DesktopSaleEntity sale, decimal amount, string status, int? sapDocEntry, string planJson = "")
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
            PlanJson = planJson,
            CreatedAtUtc = DuringDayUtc.AddMinutes(_context.DesktopCreditNotes.Count()),
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>A credit plan as the credit dialog saves it: receipt lines, 1-based, at a tax-inclusive price.</summary>
    private static string Plan(params (int ReceiptLine, string Name, decimal Quantity, decimal UnitPrice)[] credited) =>
        System.Text.Json.JsonSerializer.Serialize(
            new DesktopCreditPlan(
                new DesktopCreditSource(
                    "VAN-1", "USD", 100m, 0, 0, 0, null,
                    credited.Select(line => new DesktopCreditLine(line.ReceiptLine, line.Name, line.Quantity, line.UnitPrice, 1, 15.5m, null, null)).ToList()),
                credited.Select(line => new DesktopCreditQuantity(line.ReceiptLine, line.Quantity)).ToList(),
                null!,
                credited.Sum(line => line.Quantity * line.UnitPrice)),
            DesktopCreditNoteService.Json);

    private async Task SetBaseLine(int docEntry, int baseLine)
    {
        await _context.SapCreditNoteLineSnapshots
            .Where(line => line.CreditNoteDocEntry == docEntry)
            .ExecuteUpdateAsync(update => update.SetProperty(line => line.BaseLine, baseLine));
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
