using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the sale number the till's invoice history heads each SAP invoice with. The till used to show
/// <c>INV</c> and the SAP document entry, which the customer's receipt never says.
/// </summary>
public sealed class InvoiceSaleNumbersTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public InvoiceSaleNumbersTests()
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
    public async Task An_invoice_a_till_sale_posted_carries_that_sales_number()
    {
        var sale = await AddSale("KEF-FAC-20261015-C6B938CA7A8A");
        var invoice = Invoice(2405175, "KEF-FAC-20261015-C6B938CA7A8A");

        await InvoiceSaleNumbers.StampAsync(_context, [invoice], CancellationToken.None);

        Assert.Equal($"INV{sale.Id}", invoice.SaleNumber);
    }

    [Fact]
    public async Task Each_invoice_gets_its_own_sale_and_an_office_invoice_gets_none()
    {
        var first = await AddSale("KEF-FAC-A");
        var second = await AddSale("KEF-FAC-B");
        var invoices = new List<InvoiceDto>
        {
            Invoice(1, "KEF-FAC-B"),
            Invoice(2, "KEF-FAC-A"),
            Invoice(3, null),
            Invoice(4, "PO-4411"),
        };

        await InvoiceSaleNumbers.StampAsync(_context, invoices, CancellationToken.None);

        Assert.Equal(
            [$"INV{second.Id}", $"INV{first.Id}", null, null],
            invoices.Select(invoice => invoice.SaleNumber));
    }

    [Fact]
    public async Task A_reference_SAP_padded_with_spaces_still_finds_its_sale()
    {
        var sale = await AddSale("KEF-FAC-A");
        var invoice = Invoice(1, " KEF-FAC-A ");

        await InvoiceSaleNumbers.StampAsync(_context, [invoice], CancellationToken.None);

        Assert.Equal($"INV{sale.Id}", invoice.SaleNumber);
    }

    private static InvoiceDto Invoice(int docEntry, string? reference) =>
        new() { DocEntry = docEntry, DocNum = docEntry, VanSaleOrderNumber = reference };

    private async Task<DesktopSaleEntity> AddSale(string reference)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosShopTill",
            CardCode = "KEFGRS",
            WarehouseCode = "KEFGRS",
            DocDate = new DateTime(2026, 10, 15),
            TotalAmount = 20.51m,
            VatAmount = 2.67m,
            AmountPaid = 20.51m,
            Currency = "USD",
            CreatedAt = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc),
        };
        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        return sale;
    }
}
