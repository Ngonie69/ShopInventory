using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNotes.Queries.DownloadCreditNotePdf;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The credit note PDF, printed on the invoice's sheet: what it says that the invoice does not, what it
/// leaves off, and that the invoice itself still prints as it did.
/// </summary>
/// <remarks>Set <c>INVOICE_PDF_OUT</c> to a directory to keep the rendered PDFs.</remarks>
public sealed class CreditNotePdfTests : IDisposable
{
    private static readonly InvoicePdfService Service = new(NullLogger<InvoicePdfService>.Instance);

    private readonly SqliteConnection _connection;

    public CreditNotePdfTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ACreditNoteIsTitledAsOneAndNamesTheInvoiceAndReason()
    {
        var text = await TextAsync(
            await Service.GenerateCreditNotePdfAsync(Document(), new CreditNotePdfDetails(10431, "Wrong item delivered")),
            "credit-note");

        Assert.Contains("Credit Note", text);
        Assert.DoesNotContain("Fiscal Tax Invoice", text);
        Assert.Matches(@"CREDIT NOTE NO\.\s+30377", text);
        Assert.Matches(@"AGAINST INVOICE\s+10431", text);
        Assert.Contains("Wrong item delivered", text);
        Assert.Contains("CUSTOMER ADDRESS", text);
        Assert.Matches(@"Credit Total\s+USD 312\.00", text);
        // Nothing is owed on a credit.
        Assert.DoesNotContain("Please deposit into", text);
        Assert.DoesNotContain("Invoice Total", text);
    }

    [Fact]
    public async Task TheInvoiceStillPrintsAsItDid()
    {
        var text = await TextAsync(await Service.GenerateInvoicePdfAsync(Document()), "invoice-after-credit-change");

        Assert.Contains("Fiscal Tax Invoice", text);
        Assert.Matches(@"DOC NUMBER\s+30377", text);
        Assert.Matches(@"INVOICE NUMBER\s+30377", text);
        Assert.Contains("INVOICE ADDRESS", text);
        Assert.Contains("Please deposit into", text);
        Assert.Matches(@"Invoice Total\s+USD 312\.00", text);
        Assert.DoesNotContain("Credit Total", text);
    }

    [Fact]
    public async Task AMemoOnAnotherAccountAnswersAsNotFoundAndIsNeverRendered()
    {
        var memo = CustomerCreditNotesTests.Memo(30377, "2026-10-22T00:00:00Z", 312m, 40.70m, 0m, open: true);
        var sap = SapAnswers.Create(new Dictionary<string, Func<object?[]?, object?>>
        {
            ["GetCreditNoteByDocEntryAsync"] = _ => Task.FromResult<SAPCreditNote?>(memo)
        });

        await using var context = NewContext();
        var handler = new DownloadCreditNotePdfHandler(
            context, sap, StubProxy.Unused<IInvoicePdfService>(),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<DownloadCreditNotePdfHandler>.Instance);

        var result = await handler.Handle(new DownloadCreditNotePdfQuery(memo.DocEntry, "C99001"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ErrorOr.ErrorType.NotFound, result.FirstError.Type);
    }

    [Fact]
    public async Task TheCustomersOwnMemoPrintsWithItsCreditedInvoice()
    {
        var memo = CustomerCreditNotesTests.Memo(30377, "2026-10-22T00:00:00Z", 312m, 40.70m, 150m, open: true,
            invoiceDocEntry: 9001, reasons: ["Wrong item delivered"]);
        var sap = SapAnswers.Create(new Dictionary<string, Func<object?[]?, object?>>
        {
            ["GetCreditNoteByDocEntryAsync"] = _ => Task.FromResult<SAPCreditNote?>(memo),
            ["GetInvoiceBalancesByDocEntriesAsync"] = _ => Task.FromResult(new List<Invoice>
            {
                new() { DocEntry = 9001, DocNum = 10431, CardCode = "C20417" }
            }),
            ["GetBusinessPartnerByCodeAsync"] = _ => Task.FromResult<BusinessPartnerDto?>(null)
        });

        await using var context = NewContext();
        var handler = new DownloadCreditNotePdfHandler(
            context, sap, Service, Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<DownloadCreditNotePdfHandler>.Instance);

        var result = await handler.Handle(new DownloadCreditNotePdfQuery(memo.DocEntry, " c20417 "), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal("CreditNote_30377.pdf", result.Value.FileName);

        var text = await TextAsync(result.Value.PdfBytes, "credit-note-from-handler");
        Assert.Matches(@"AGAINST INVOICE\s+10431", text);
        Assert.Contains("Wrong item delivered", text);
    }

    private static InvoiceDto Document() => new()
    {
        DocEntry = 61377,
        DocNum = 30377,
        DocDate = "2026-10-22",
        CardCode = "C20417",
        CardName = "Greenfield Supermarket",
        DocCurrency = "USD",
        DocTotal = 312.00m,
        VatSum = 40.70m,
        Lines =
        [
            new InvoiceLineDto { ItemCode = "MOZ-2000", ItemDescription = "Mozzarella 2 kg", Quantity = 12,
                UnitPrice = 16.10m, LineTotal = 193.20m, GrossTotal = 222.00m },
            new InvoiceLineDto { ItemCode = "FET-400", ItemDescription = "Feta 400 g", Quantity = 18,
                UnitPrice = 4.339m, LineTotal = 78.10m, GrossTotal = 90.00m }
        ]
    };

    private static async Task<string> TextAsync(byte[] bytes, string name)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("INVOICE_PDF_OUT");
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, name + ".pdf"), bytes);
        }

        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        return string.Join("\n", Enumerable.Range(1, pdf.GetNumberOfPages())
            .Select(page => PdfTextExtractor.GetTextFromPage(pdf.GetPage(page))));
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
}
