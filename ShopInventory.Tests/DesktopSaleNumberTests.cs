using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The short number a sale is picked by, and the search that finds it.
///
/// What this is for: a till reference reads <c>KEF-FAC-20260915-1F3EBB3AD785</c>, and an operator
/// crediting a return had no other way to name a sale. Twelve hex characters cannot be read off a
/// customer's receipt and matched against a list by eye, so the one thing that has to hold is that the
/// number the till printed is the number the console finds — searched for exactly as printed, and
/// searched for the way a person actually types it.
/// </summary>
public sealed class DesktopSaleNumberTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly int _shopId;

    public DesktopSaleNumberTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        var shop = new ShopEntity
        {
            Code = "KEF",
            Name = "Kefalos",
            BusinessPartnerCode = "KEF-BP",
            WarehouseCode = "KEFSHOP",
            IsActive = true,
        };
        _context.Shops.Add(shop);
        _context.SaveChanges();
        _shopId = shop.Id;
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- The format ------------------------------------------------------------------------------

    [Fact]
    public void A_sale_number_is_the_row_id_in_the_format_the_till_prints()
    {
        // Pinned against the till, not chosen here. KefalosTill's ReceiptRenderer.Transaction writes
        // $"INV{receipt.InvoiceId}" where InvoiceId is the saleId this API returned, so this string is
        // already on every customer receipt. A change here silently un-matches all of them.
        Assert.Equal("INV10427", DesktopSaleNumber.Format(10427));
    }

    [Theory]
    // As printed, and as a person types it in a hurry: the prefix is optional, so is its hyphen, so is
    // the case, and a hash in front is what someone reaching for "number" types.
    [InlineData("INV10427")]
    [InlineData("inv10427")]
    [InlineData("INV-10427")]
    [InlineData("inv 10427")]
    [InlineData("#10427")]
    [InlineData("10427")]
    [InlineData("  INV10427  ")]
    public void A_sale_number_is_read_the_way_it_is_typed(string typed)
    {
        Assert.True(DesktopSaleNumber.TryParse(typed, out var saleId));
        Assert.Equal(10427, saleId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("INV")]
    [InlineData("KEF-FAC-20260915-1F3EBB3AD785")]
    [InlineData("0")]
    // Negatives name no row. Left to int.TryParse alone, a stray hyphen would parse and search for a
    // sale id that cannot exist, which returns nothing and looks like the sale is missing.
    [InlineData("-5")]
    [InlineData("INV0")]
    public void Text_that_is_not_a_sale_number_is_refused(string? typed)
    {
        Assert.False(DesktopSaleNumber.TryParse(typed, out var saleId));
        Assert.Equal(0, saleId);
    }

    // ---- The list carries it ---------------------------------------------------------------------

    [Fact]
    public async Task Every_listed_sale_carries_its_number_formatted()
    {
        var sale = await AddSale("KEF-FAC-20260915-1F3EBB3AD785");
        var caller = await AddTillOperator();

        var result = await List(caller);

        Assert.False(result.IsError);
        var listed = Assert.Single(result.Value.Sales);
        Assert.Equal(DesktopSaleNumber.Format(sale.Id), listed.SaleNumber);
        // The device reference is not replaced by it: fiscal and support work is keyed on that string.
        Assert.Equal("KEF-FAC-20260915-1F3EBB3AD785", listed.ExternalReferenceId);
    }

    // ---- The search finds it ---------------------------------------------------------------------

    [Theory]
    [InlineData("INV{0}")]
    [InlineData("inv{0}")]
    [InlineData("#{0}")]
    [InlineData("{0}")]
    public async Task A_sale_is_found_by_the_number_the_receipt_printed(string typedFormat)
    {
        var wanted = await AddSale("KEF-FAC-20260915-1F3EBB3AD785");
        await AddSale("KEF-FAC-20260915-DE72881AC389");
        var caller = await AddTillOperator();

        var result = await List(caller, search: string.Format(typedFormat, wanted.Id));

        Assert.False(result.IsError);
        Assert.Equal(wanted.Id, Assert.Single(result.Value.Sales).Id);
        Assert.Equal(1, result.Value.TotalCount);
    }

    [Fact]
    public async Task A_search_that_names_the_prefix_does_not_also_match_a_sap_document()
    {
        // SAP document numbers are in the 770000s and sale ids start at 1, so today the two ranges do
        // not meet — but they are both plain integers and they will. "INV" says which is meant, and a
        // search that says it must not drag in the other sale as well.
        var sale = await AddSale("KEF-FAC-20260915-1F3EBB3AD785");
        var decoy = await AddSale("KEF-FAC-20260915-DE72881AC389", sapDocNum: sale.Id);
        var caller = await AddTillOperator();

        var result = await List(caller, search: DesktopSaleNumber.Format(sale.Id));

        Assert.False(result.IsError);
        Assert.Equal(sale.Id, Assert.Single(result.Value.Sales).Id);
        Assert.DoesNotContain(result.Value.Sales, s => s.Id == decoy.Id);
    }

    [Fact]
    public async Task A_bare_number_still_finds_the_sap_invoice_it_posted_as()
    {
        // The other thing a person can be holding. Adding the sale number to the search must not cost
        // the search that was already there.
        await AddSale("KEF-FAC-20260915-1F3EBB3AD785");
        var posted = await AddSale("KEF-FAC-20260915-DE72881AC389", sapDocNum: 774531);
        var caller = await AddTillOperator();

        var result = await List(caller, search: "774531");

        Assert.False(result.IsError);
        Assert.Equal(posted.Id, Assert.Single(result.Value.Sales).Id);
    }

    [Fact]
    public async Task The_device_reference_is_still_searchable_in_part()
    {
        var wanted = await AddSale("KEF-FAC-20260915-1F3EBB3AD785");
        await AddSale("KEF-FAC-20260915-DE72881AC389");
        var caller = await AddTillOperator();

        var result = await List(caller, search: "1f3ebb3ad785");

        Assert.False(result.IsError);
        Assert.Equal(wanted.Id, Assert.Single(result.Value.Sales).Id);
    }

    [Fact]
    public async Task The_fiscal_receipt_number_is_still_searchable()
    {
        // What the customer's receipt carries beside the sale number, and the search a person falls
        // back to when the printed number has been torn off.
        await AddSale("KEF-FAC-20260915-1F3EBB3AD785");
        var wanted = await AddSale("KEF-FAC-20260915-DE72881AC389", receiptNumber: "216877");
        var caller = await AddTillOperator();

        var result = await List(caller, search: "216877");

        Assert.False(result.IsError);
        Assert.Equal(wanted.Id, Assert.Single(result.Value.Sales).Id);
    }

    [Fact]
    public async Task A_sale_number_that_names_no_row_finds_nothing_rather_than_everything()
    {
        await AddSale("KEF-FAC-20260915-1F3EBB3AD785");
        var caller = await AddTillOperator();

        var result = await List(caller, search: "INV999999");

        Assert.False(result.IsError);
        Assert.Empty(result.Value.Sales);
        Assert.Equal(0, result.Value.TotalCount);
    }

    // ---- Harness ---------------------------------------------------------------------------------

    // Ids are assigned explicitly and in the range production is in. A row whose id is 1 or 2 makes
    // every assertion about a bare-number search meaningless: "1" is a substring of nearly every
    // device reference ever issued, so such a test passes or fails on the decoy data rather than on
    // the search.
    private int _nextId = 10427;

    private async Task<DesktopSaleEntity> AddSale(
        string reference, int? sapDocNum = null, string? receiptNumber = null)
    {
        var sale = new DesktopSaleEntity
        {
            Id = _nextId++,
            ExternalReferenceId = reference,
            SourceSystem = "KefShop",
            CardCode = "KEF-BP",
            WarehouseCode = "KEFSHOP",
            DocDate = new DateTime(2026, 9, 15),
            TotalAmount = 100m,
            VatAmount = 13m,
            AmountPaid = 100m,
            Currency = "USD",
            SapDocNum = sapDocNum,
            FiscalReceiptNumber = receiptNumber,
            CreatedAt = new DateTime(2026, 9, 15, 15, 30, 0, DateTimeKind.Utc),
        };
        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return sale;
    }

    private async Task<Guid> AddTillOperator()
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = $"u{id:N}"[..12],
            PasswordHash = "x",
            Role = ApplicationRoles.TillOperator,
            IsActive = true,
            ShopId = _shopId,
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private Task<ErrorOr.ErrorOr<DesktopSalesListResult>> List(Guid callerId, string? search = null) =>
        new GetDesktopSalesHandler(
                _context,
                new RecordingAuditService(),
                Microsoft.Extensions.Options.Options.Create(new ShopInventory.Configuration.FiscalisationSettings()))
            .Handle(new GetDesktopSalesQuery(callerId, Search: search), CancellationToken.None);
}
