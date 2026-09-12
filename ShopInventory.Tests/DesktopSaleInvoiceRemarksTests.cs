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
/// What a system-posted desktop sale invoice says in its SAP Remarks.
/// </summary>
/// <remarks>
/// Till invoices used to reach SAP with the column blank: the till route passed no comments at all,
/// and the client leaves a null out of the payload. The van route wrote a line of its own that read
/// the receipt number from a column the till never fills. Both now share one builder, and these pin
/// what it says, what it gives up when the column is short, and that both routes actually send it.
/// </remarks>
public sealed class DesktopSaleInvoiceRemarksTests : IDisposable
{
    private const string Reference = "GRC-FAC-20260910-0A519807CF88";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public DesktopSaleInvoiceRemarksTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---------------------------------------------------------------
    // The text
    // ---------------------------------------------------------------

    [Fact]
    public void A_till_invoice_says_where_it_came_from_which_receipt_who_and_how_it_was_paid()
    {
        var remarks = DesktopSaleInvoiceRemarks.Build(
            TillSale(), new DesktopSaleRemarkNames("Greystone", "Tendai Moyo"));

        Assert.Equal(
            "Shop till, Greystone (KEFGRS) | Ref GRC-FAC-20260910-0A519807CF88 | "
            + "Fiscal device 8DE6996C0188, day 524, receipt 1187, code 4483-3DDB-7201-5905 | "
            + "Captured by Tendai Moyo | Paid Cash",
            remarks);

        Assert.True(remarks.Length <= DesktopSaleInvoiceRemarks.MaxLength);
    }

    [Fact]
    public void A_till_receipt_number_is_read_from_the_column_the_till_writes()
    {
        // The till route fills FiscalReceiptNumber and never ReceiptGlobalNo. The van line this
        // replaces read only ReceiptGlobalNo, which is how a till invoice would lose its receipt.
        var sale = TillSale();
        sale.ReceiptGlobalNo = null;

        var remarks = DesktopSaleInvoiceRemarks.Build(sale, DesktopSaleRemarkNames.None);

        Assert.Contains("receipt 1187", remarks);
    }

    [Fact]
    public void A_van_receipt_number_still_comes_through()
    {
        var sale = TillSale();
        sale.SourceSystem = SaleSourceSystems.VanSales;
        sale.WarehouseCode = "VAN006";
        sale.FiscalReceiptNumber = null;
        sale.ReceiptGlobalNo = 501;

        var remarks = DesktopSaleInvoiceRemarks.Build(sale, DesktopSaleRemarkNames.None);

        Assert.StartsWith("Van sale, VAN006 | ", remarks);
        Assert.Contains("receipt 501", remarks);
    }

    [Fact]
    public void A_sale_that_was_never_fiscalised_says_so()
    {
        // Said outright, so the reader does not go looking for a receipt that was never meant to exist.
        var sale = TillSale();
        sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Skipped;

        var remarks = DesktopSaleInvoiceRemarks.Build(sale, DesktopSaleRemarkNames.None);

        Assert.Contains("Not fiscalised", remarks);
        Assert.DoesNotContain("Fiscal device", remarks);
    }

    [Fact]
    public void A_code_stored_already_hyphenated_is_not_regrouped()
    {
        var sale = TillSale();
        sale.FiscalVerificationCode = "4483-3DDB-7201-5905";

        var remarks = DesktopSaleInvoiceRemarks.Build(sale, DesktopSaleRemarkNames.None);

        Assert.Contains("code 4483-3DDB-7201-5905", remarks);
    }

    [Fact]
    public void Without_names_the_warehouse_code_stands_in_and_no_cashier_is_shown()
    {
        var remarks = DesktopSaleInvoiceRemarks.Build(TillSale(), DesktopSaleRemarkNames.None);

        Assert.StartsWith("Shop till, KEFGRS | Ref ", remarks);
        Assert.DoesNotContain("Captured by", remarks);
    }

    [Fact]
    public void When_the_column_is_short_payment_and_cashier_go_before_the_receipt()
    {
        // Long enough that dropping the payment alone is not enough, so the cashier has to go too.
        var sale = TillSale();
        sale.PaymentReference = new string('R', 60);

        var remarks = DesktopSaleInvoiceRemarks.Build(
            sale, new DesktopSaleRemarkNames("Greystone", new string('N', 120)));

        Assert.True(remarks.Length <= DesktopSaleInvoiceRemarks.MaxLength, $"{remarks.Length} characters");

        // The fiscal receipt is the join to ZIMRA and nothing else on the document carries it.
        Assert.Contains("Fiscal device 8DE6996C0188, day 524, receipt 1187, code 4483-3DDB-7201-5905", remarks);
        Assert.Contains($"Ref {Reference}", remarks);
        Assert.DoesNotContain("Paid", remarks);
        Assert.DoesNotContain("Captured by", remarks);
    }

    // ---------------------------------------------------------------
    // The names
    // ---------------------------------------------------------------

    [Fact]
    public async Task The_cashier_is_the_capturing_account_s_name_and_not_its_id()
    {
        // CreatedBy is the account's id. The drawer shows it raw; a remark must not.
        var userId = await GivenUserAsync(firstName: "Tendai", lastName: "Moyo");
        await GivenShopAsync("GRS", "Greystone", "KEFGRS");

        var sale = TillSale();
        sale.CreatedBy = userId.ToString();

        var names = await DesktopSaleInvoiceRemarks.ResolveNamesAsync(
            _context, sale, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("Tendai Moyo", names.CapturedBy);
        Assert.Equal("Greystone", names.ShopName);
    }

    [Fact]
    public async Task An_account_with_no_name_set_shows_its_username()
    {
        var userId = await GivenUserAsync(firstName: null, lastName: null);

        var sale = TillSale();
        sale.CreatedBy = userId.ToString();

        var names = await DesktopSaleInvoiceRemarks.ResolveNamesAsync(
            _context, sale, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("grs.till", names.CapturedBy);
    }

    [Fact]
    public async Task An_account_that_no_longer_exists_shows_nothing_rather_than_an_id()
    {
        var sale = TillSale();
        sale.CreatedBy = Guid.NewGuid().ToString();

        var names = await DesktopSaleInvoiceRemarks.ResolveNamesAsync(
            _context, sale, NullLogger.Instance, CancellationToken.None);

        Assert.Null(names.CapturedBy);
    }

    [Fact]
    public async Task An_active_shop_is_preferred_when_two_share_a_warehouse()
    {
        // WarehouseCode is indexed but not unique.
        await GivenShopAsync("OLD", "Old Greystone", "KEFGRS", isActive: false);
        await GivenShopAsync("GRS", "Greystone", "KEFGRS");

        var names = await DesktopSaleInvoiceRemarks.ResolveNamesAsync(
            _context, TillSale(), NullLogger.Instance, CancellationToken.None);

        Assert.Equal("Greystone", names.ShopName);
    }

    // ---------------------------------------------------------------
    // Both routes send it
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_posted_till_invoice_carries_the_remarks()
    {
        var userId = await GivenUserAsync(firstName: "Tendai", lastName: "Moyo");
        await GivenShopAsync("GRS", "Greystone", "KEFGRS");

        var sale = TillSale();
        sale.CreatedBy = userId.ToString();
        sale.DocDate = DateTime.UtcNow.Date;
        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();

        var sap = new RecordingSapClient();
        var result = await new DesktopSalePostingService(
            _context,
            sap.Client,
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            SaleBatchAllocators.Holding(),
            SalePostGuards.Backed(_connection),
            DesktopCreditPosters.Idle(_context),
            Options.Create(new DesktopSalePostingSettings()),
            Options.Create(new SAPSettings()),
            NullLogger<DesktopSalePostingService>.Instance).PostPendingSalesAsync();

        Assert.Equal(1, result.Posted);

        // Asserted on what reaches the SAP client, not on the builder: the blank column was a route
        // that never passed anything, and only the posted request can show that is fixed.
        Assert.Equal(
            "Shop till, Greystone (KEFGRS) | Ref GRC-FAC-20260910-0A519807CF88 | "
            + "Fiscal device 8DE6996C0188, day 524, receipt 1187, code 4483-3DDB-7201-5905 | "
            + "Captured by Tendai Moyo | Paid Cash",
            Assert.Single(sap.Created).Comments);
    }

    [Fact]
    public async Task A_posted_van_invoice_carries_the_same_remarks()
    {
        var today = DateTime.UtcNow.Date;

        var sale = TillSale();
        sale.SourceSystem = SaleSourceSystems.VanSales;
        sale.WarehouseCode = "VAN006";
        sale.Lines.Single().WarehouseCode = "VAN006";
        sale.DocDate = today;
        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();

        var sap = new RecordingSapClient();
        var result = await new VanSalesEndOfDayPostingService(
            _context,
            sap.Client,
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            SaleBatchAllocators.Holding(),
            new StockLedger(_context, Options.Create(new DailyStockSettings()), NullLogger<StockLedger>.Instance),
            SalePostGuards.Backed(_connection),
            Options.Create(new VanSalesPostingSettings()),
            NullLogger<VanSalesEndOfDayPostingService>.Instance).PostPendingSalesAsync(today);

        Assert.Equal(1, result.Posted);

        var comments = Assert.Single(sap.Created).Comments;
        Assert.NotNull(comments);
        Assert.StartsWith("Van sale, VAN006 | Ref ", comments);
        Assert.Contains("receipt 1187", comments);
    }

    // ---------------------------------------------------------------

    private static DesktopSaleEntity TillSale() => new()
    {
        ExternalReferenceId = Reference,
        SourceSystem = SaleSourceSystems.ShopTill,
        CardCode = "COR007",
        DocDate = new DateTime(2026, 9, 10),
        TotalAmount = 7.22m,
        VatAmount = 0.97m,
        Currency = "USD",
        WarehouseCode = "KEFGRS",
        FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
        ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
        FiscalDeviceNumber = "8DE6996C0188",
        FiscalDayNo = "524",
        FiscalReceiptNumber = "1187",
        FiscalVerificationCode = "44833DDB72015905",
        PaymentMethod = "Cash",
        // Zero, so the settlement step stays out of the way and these stay about the invoice.
        AmountPaid = 0m,
        Lines =
        [
            new DesktopSaleLineEntity
            {
                LineNum = 0,
                ItemCode = "CHE011",
                Quantity = 2m,
                UnitPrice = 3.61m,
                LineTotal = 7.22m,
                WarehouseCode = "KEFGRS"
            }
        ]
    };

    private async Task<Guid> GivenUserAsync(string? firstName, string? lastName)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "grs.till",
            PasswordHash = "not-a-real-hash",
            Role = "TillOperator",
            FirstName = firstName,
            LastName = lastName
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user.Id;
    }

    private async Task GivenShopAsync(string code, string name, string warehouseCode, bool isActive = true)
    {
        _context.Shops.Add(new ShopEntity
        {
            Code = code,
            Name = name,
            BusinessPartnerCode = "COR007",
            WarehouseCode = warehouseCode,
            IsActive = isActive
        });

        await _context.SaveChangesAsync();
    }

    private sealed class RecordingSapClient
    {
        private int _nextDocNum = 9000;

        public List<CreateInvoiceRequest> Created { get; } = [];

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) =>
                (object)Task.FromResult<Invoice?>(null),

            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => Create((CreateInvoiceRequest)args![0]!),

            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        private Task<Invoice> Create(CreateInvoiceRequest request)
        {
            Created.Add(request);
            var docNum = _nextDocNum++;
            return Task.FromResult(new Invoice { DocEntry = docNum, DocNum = docNum });
        }
    }
}
