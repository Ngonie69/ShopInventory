using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Which number a credit note asks the fiscal device for, to find the receipt it reverses.
/// </summary>
/// <remarks>
/// Measured on 2026-09-11 against device 22862: till invoice 772109 is DocEntry 2342939, the device
/// answers "Invoice not Found" for both 772109 and 2342939, and holds receipt 216877 under the sale's
/// own reference GRC-FAC-20260911-286EEC7389FD. A credit note that asks under the wrong number is
/// refused as having no original, so it never reaches ZIMRA.
/// </remarks>
public sealed class CreditNoteOriginalReceiptTests : IDisposable
{
    private static readonly FiscalisationSettings Settings = new() { PreSapInvoiceNoPrefix = "SI-" };

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public CreditNoteOriginalReceiptTests()
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

    [Fact]
    public async Task An_ordinary_invoice_is_looked_up_under_its_DocNum()
    {
        var resolved = await ResolveAsync(772500);

        Assert.Equal("772500", resolved.InvoiceNumber);
        Assert.Null(resolved.Refusal);
    }

    [Fact]
    public async Task A_till_sale_is_looked_up_under_the_reference_it_was_fiscalised_under()
    {
        await SeedSaleAsync("GRC-FAC-20260911-286EEC7389FD", docNum: 772109);

        var resolved = await ResolveAsync(772109);

        Assert.Equal("GRC-FAC-20260911-286EEC7389FD", resolved.InvoiceNumber);
    }

    [Fact]
    public async Task A_numeric_sale_reference_is_looked_up_with_the_prefix_it_was_filed_with()
    {
        await SeedSaleAsync("10234", docNum: 772110);

        var resolved = await ResolveAsync(772110);

        Assert.Equal("SI-10234", resolved.InvoiceNumber);
    }

    [Fact]
    public async Task A_sale_that_never_fiscalised_is_not_the_original()
    {
        // Nothing was filed under the sale's reference, so the invoice keeps its own number: that is
        // where a receipt for it would be if someone fiscalised the invoice by hand.
        await SeedSaleAsync("GRC-FAC-20260911-000000000000", docNum: 772111, DesktopSaleFiscalizationStatus.Failed);

        var resolved = await ResolveAsync(772111);

        Assert.Equal("772111", resolved.InvoiceNumber);
    }

    [Fact]
    public async Task A_consolidated_invoice_has_no_single_receipt_to_reference()
    {
        _context.SaleConsolidations.Add(new SaleConsolidationEntity
        {
            CardCode = "COR007",
            SapDocNum = 772200,
            SaleCount = 14
        });
        await _context.SaveChangesAsync();

        var resolved = await ResolveAsync(772200);

        Assert.Null(resolved.InvoiceNumber);
        Assert.Contains("14 sale(s)", resolved.Refusal);
    }

    [Fact]
    public async Task An_unknown_document_number_is_refused_rather_than_guessed()
    {
        var resolved = await ResolveAsync(0);

        Assert.Null(resolved.InvoiceNumber);
        Assert.NotNull(resolved.Refusal);
    }

    private Task<CreditNoteOriginalReceiptNumber> ResolveAsync(int docNum)
        => CreditNoteOriginalReceipt.ResolveAsync(_context, docNum, Settings, CancellationToken.None);

    private async Task SeedSaleAsync(
        string externalReference,
        int docNum,
        DesktopSaleFiscalizationStatus status = DesktopSaleFiscalizationStatus.Success)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = externalReference,
            CardCode = "COR007",
            WarehouseCode = "KEFGRS",
            SapDocNum = docNum,
            FiscalizationStatus = status
        });
        await _context.SaveChangesAsync();
    }
}
