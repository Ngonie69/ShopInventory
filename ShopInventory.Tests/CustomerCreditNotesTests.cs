using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.CreditNotes;
using ShopInventory.Features.CreditNotes.Queries.GetCustomerCreditNotes;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The customer portal's credit-note list: what each memo states, in whose currency, and whose
/// fiscal receipt it may carry.
/// </summary>
public sealed class CustomerCreditNotesTests : IDisposable
{
    private const string Customer = "C20417";
    private const string OtherCustomer = "C99001";

    private readonly SqliteConnection _connection;

    public CustomerCreditNotesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task EachNoteStatesWhatIsUsedAndLeftInItsOwnCurrency()
    {
        var result = await HandleAsync(
            [
                Memo(30377, "2026-10-22T00:00:00Z", total: 312.00m, vat: 40.70m, paid: 150.00m, open: true,
                    invoiceDocEntry: 9001, reasons: ["Wrong item delivered"]),
                // ZiG at 30: SAP's plain fields carry the USD value, the Fc field the ZiG.
                Memo(30402, "2026-10-30T00:00:00Z", total: 10.00m, vat: 1.30m, paid: 0m, open: true,
                    currency: "ZiG", foreignTotal: 300.00m),
                // Closed against its invoice: used up whatever PaidToDate reads.
                Memo(30311, "2026-10-02T00:00:00Z", total: 77.60m, vat: 10.12m, paid: 0m, open: false)
            ],
            invoices: [new Invoice { DocEntry = 9001, DocNum = 10431, CardCode = Customer }]);

        Assert.Equal([30402, 30377, 30311], result.CreditNotes.Select(note => note.DocNum));

        var partApplied = result.CreditNotes.Single(note => note.DocNum == 30377);
        Assert.Equal(312.00m, partApplied.DocTotal);
        Assert.Equal(150.00m, partApplied.AppliedAmount);
        Assert.Equal(162.00m, partApplied.CreditLeft);
        Assert.Equal(10431, partApplied.CreditedInvoiceDocNum);
        Assert.Equal("Wrong item delivered", partApplied.Reason);

        var zig = result.CreditNotes.Single(note => note.DocNum == 30402);
        Assert.Equal("ZiG", zig.Currency);
        Assert.Equal(300.00m, zig.DocTotal);
        Assert.Equal(39.00m, zig.VatTotal);
        Assert.Equal(300.00m, zig.CreditLeft);
        Assert.Equal(0, zig.CreditedInvoiceDocNum);

        var closed = result.CreditNotes.Single(note => note.DocNum == 30311);
        Assert.Equal(77.60m, closed.AppliedAmount);
        Assert.Equal(0m, closed.CreditLeft);
    }

    [Fact]
    public async Task CancelledMemosTheirCancellationsAndOtherAccountsAreLeftOut()
    {
        var cancelled = Memo(30350, "2026-10-15T00:00:00Z", 40m, 5.22m, 0m, open: false);
        cancelled.Cancelled = "tYES";
        var cancellation = Memo(30351, "2026-10-15T00:00:00Z", 40m, 5.22m, 0m, open: false);
        cancellation.CancelStatus = "csCancellation";
        var stray = Memo(30352, "2026-10-16T00:00:00Z", 25m, 3.26m, 0m, open: true);
        stray.CardCode = OtherCustomer;

        var result = await HandleAsync(
            [cancelled, cancellation, stray, Memo(30360, "2026-10-17T00:00:00Z", 140m, 18.26m, 140m, open: false)],
            invoices: []);

        Assert.Equal(30360, Assert.Single(result.CreditNotes).DocNum);
    }

    [Fact]
    public async Task TheReasonIsTheLinesListNeverTheStaffComment()
    {
        var memo = Memo(30418, "2026-11-04T00:00:00Z", 186.40m, 24.31m, 0m, open: true,
            reasons: ["Short-dated", "Damaged", "short-dated"]);
        memo.Comments = "Rep says customer was rude - do not extend credit";

        var note = Assert.Single((await HandleAsync([memo], invoices: [])).CreditNotes);

        Assert.Equal("Short-dated; Damaged", note.Reason);
        Assert.DoesNotContain(note.Lines, line => line.Description!.Contains("rude"));
    }

    [Fact]
    public async Task AnInvoiceOnAnotherAccountIsNotNamed()
    {
        var note = Assert.Single((await HandleAsync(
            [Memo(30377, "2026-10-22T00:00:00Z", 312m, 40.70m, 0m, open: true, invoiceDocEntry: 9001)],
            invoices: [new Invoice { DocEntry = 9001, DocNum = 10431, CardCode = OtherCustomer }])).CreditNotes);

        Assert.Equal(0, note.CreditedInvoiceDocNum);
    }

    [Fact]
    public async Task AReceiptLoggedUnderTheSameNumberForAnotherCustomerIsNotShown()
    {
        // SAP reissued numbers in September 2026: 30377 is also a memo on another account, and that
        // memo's receipt is the only one logged under the number.
        await LogReceiptAsync(30377, OtherCustomer, 55.00m, receiptNo: 41001, qr: "https://fdms.zimra.co.zw/other");

        var note = Assert.Single((await HandleAsync(
            [Memo(30377, "2026-10-22T00:00:00Z", 312m, 40.70m, 0m, open: true)], invoices: [])).CreditNotes);

        Assert.False(note.IsFiscalised);
        Assert.Equal(0, note.FiscalReceiptNumber);
        Assert.Null(note.FiscalVerificationUrl);
    }

    [Fact]
    public async Task TheCustomersOwnReceiptIsShownBesideAnotherWithTheSameNumber()
    {
        await LogReceiptAsync(30377, OtherCustomer, 55.00m, receiptNo: 41001, qr: "https://fdms.zimra.co.zw/other");
        await LogReceiptAsync(30377, Customer, 312.00m, receiptNo: 47902, qr: "https://fdms.zimra.co.zw/mine",
            verificationCode: "AB12CD34EF567890");

        var note = Assert.Single((await HandleAsync(
            [Memo(30377, "2026-10-22T00:00:00Z", 312m, 40.70m, 0m, open: true)], invoices: [])).CreditNotes);

        Assert.True(note.IsFiscalised);
        Assert.Equal(47902, note.FiscalReceiptNumber);
        Assert.Equal("https://fdms.zimra.co.zw/mine", note.FiscalVerificationUrl);
        Assert.Equal("AB12-CD34-EF56-7890", note.FiscalVerificationCode);
    }

    [Theory]
    [InlineData(312.00, true)]
    [InlineData(-312.00, true)]
    [InlineData(312.01, true)]
    [InlineData(55.00, false)]
    public void ARowWithNoAccountIsTheMemosOnlyWhenItsTotalAgrees(double loggedTotal, bool belongs)
    {
        var row = new DesktopFiscalTransactionEntity { DocNum = 30377, CardCode = " ", DocTotal = (decimal)loggedTotal };

        Assert.Equal(belongs, CreditNoteReceiptLookup.BelongsTo(
            row, Memo(30377, "2026-10-22T00:00:00Z", 312m, 40.70m, 0m, open: true)));
    }

    [Fact]
    public void APeriodOverAYearIsRefused()
    {
        var validator = new GetCustomerCreditNotesValidator();

        // 366 days is the most allowed; one more is refused.
        Assert.True(validator.Validate(new GetCustomerCreditNotesQuery([Customer], new(2025, 11, 5), new(2026, 11, 6))).IsValid);
        Assert.False(validator.Validate(new GetCustomerCreditNotesQuery([Customer], new(2025, 11, 4), new(2026, 11, 6))).IsValid);
        Assert.False(validator.Validate(new GetCustomerCreditNotesQuery([Customer], new(2026, 11, 6), new(2026, 11, 5))).IsValid);
        Assert.False(validator.Validate(new GetCustomerCreditNotesQuery([], new(2026, 10, 1), new(2026, 11, 6))).IsValid);
    }

    private async Task<CustomerCreditNotesResult> HandleAsync(List<SAPCreditNote> memos, List<Invoice> invoices)
    {
        var sap = SapAnswers.Create(new Dictionary<string, Func<object?[]?, object?>>
        {
            ["GetCreditNotesByCustomersAsync"] = _ => Task.FromResult(memos),
            ["GetInvoiceBalancesByDocEntriesAsync"] = _ => Task.FromResult(invoices)
        });

        await using var context = NewContext();
        var handler = new GetCustomerCreditNotesHandler(
            context, sap, Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetCustomerCreditNotesHandler>.Instance);

        var result = await handler.Handle(
            new GetCustomerCreditNotesQuery([Customer], new DateTime(2026, 10, 1), new DateTime(2026, 11, 6)),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task LogReceiptAsync(int docNum, string cardCode, decimal total, int receiptNo, string qr,
        string? verificationCode = null)
    {
        await using var context = NewContext();
        context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = Guid.NewGuid().ToString("N"),
            DocumentType = "CreditNote",
            DocNum = docNum,
            Status = "Success",
            CardCode = cardCode,
            DocTotal = total,
            ReceiptGlobalNo = receiptNo,
            QRCode = qr,
            VerificationCode = verificationCode
        });
        await context.SaveChangesAsync();
    }

    internal static SAPCreditNote Memo(
        int docNum, string date, decimal total, decimal vat, decimal paid, bool open,
        string currency = "USD", decimal foreignTotal = 0m, int? invoiceDocEntry = null, string[]? reasons = null) => new()
    {
        DocEntry = docNum + 31000,
        DocNum = docNum,
        DocDate = date,
        CardCode = Customer,
        CardName = "Greenfield Supermarket",
        DocCurrency = currency,
        DocTotal = total,
        DocTotalFc = foreignTotal,
        VatSum = vat,
        PaidToDate = paid,
        DocumentStatus = open ? "bost_Open" : "bost_Close",
        Cancelled = "tNO",
        CancelStatus = "csNo",
        DocumentLines = (reasons ?? [null!])
            .Select((reason, index) => new SAPCreditNoteLine
            {
                LineNum = index,
                ItemCode = $"ITEM-{index}",
                ItemDescription = $"Item {index}",
                Quantity = 1,
                UnitPrice = total - vat,
                PriceAfterVAT = total,
                LineTotal = total - vat,
                GrossTotal = total,
                BaseType = invoiceDocEntry is null ? null : 13,
                BaseEntry = invoiceDocEntry,
                CreditReason = reason
            })
            .ToList()
    };

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
}
