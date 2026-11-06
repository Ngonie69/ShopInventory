using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The statement PDF's aging section, read back out of a real rendered PDF.
/// </summary>
/// <remarks>
/// When SAP cannot answer the aging read, the statement carries empty buckets. Printed as they
/// were, the PDF said "Total Due 0.00" for an account that owed money.
/// </remarks>
public sealed class CustomerStatementPdfAgingTests
{
    [Fact]
    public async Task Unavailable_aging_prints_a_note_instead_of_zero_buckets()
    {
        var text = await RenderAsync(agingAvailable: false);

        Assert.Contains("AGING ANALYSIS", text);
        Assert.Contains("Aging could not be loaded", text);
        Assert.DoesNotContain("Total Due", text);
    }

    [Fact]
    public async Task Available_aging_still_prints_its_table()
    {
        var text = await RenderAsync(agingAvailable: true);

        Assert.Contains("Total Due", text);
        Assert.Contains("0-30 Days", text);
        Assert.DoesNotContain("Aging could not be loaded", text);
    }

    private static async Task<string> RenderAsync(bool agingAvailable)
    {
        var statement = new CustomerStatementResponseDto
        {
            Customer = new StatementCustomerDto { CardCode = "C100", CardName = "Customer", Currency = "USD" },
            FromDate = new DateTime(2026, 10, 1),
            ToDate = new DateTime(2026, 10, 31),
            OpeningBalance = 1000m,
            ClosingBalance = 1400m,
            Lines =
            [
                new StatementLineDto
                {
                    Date = new DateTime(2026, 10, 6),
                    TransactionNumber = 501,
                    OriginCode = "IN",
                    DocumentType = "Invoice",
                    DocumentNumber = "84565",
                    Description = "Invoice",
                    Debit = 400m,
                    Balance = 1400m
                }
            ],
            Aging = new StatementAgingSummaryDto
            {
                AsAtDate = new DateTime(2026, 10, 31),
                Bucket1Label = "0-30 Days",
                Bucket2Label = "31-60 Days",
                Bucket3Label = "61-90 Days",
                Bucket4Label = "Over 90 Days",
                Current = agingAvailable ? 400m : 0m,
                Total = agingAvailable ? 1400m : 0m
            },
            AgingAvailable = agingAvailable
        };

        var bytes = await new StatementService(NullLogger<StatementService>.Instance)
            .GenerateCustomerStatementAsync(statement);

        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        var text = new StringBuilder();
        for (var page = 1; page <= pdf.GetNumberOfPages(); page++)
        {
            text.AppendLine(PdfTextExtractor.GetTextFromPage(pdf.GetPage(page)));
        }

        return text.ToString();
    }
}
