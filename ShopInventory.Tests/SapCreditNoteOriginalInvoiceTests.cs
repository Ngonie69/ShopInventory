using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNotes;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Which invoice a SAP credit memo reverses. It must be the one the fiscal platform picks
/// (<c>SapCreditNoteLinkResolver</c>): the two file the same memos, and a credit note filed against the
/// wrong receipt cannot be withdrawn.
/// </summary>
public sealed class SapCreditNoteOriginalInvoiceTests : IDisposable
{
    private const string Customer = "C10001";
    private const string OtherCustomer = "C20002";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public SapCreditNoteOriginalInvoiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── Lines copied from an invoice ─────────────────────────────────────────────

    [Fact]
    public async Task Lines_copied_from_one_invoice_file_against_it_and_Customer_Ref_No_is_not_read()
    {
        var sap = new FakeSap();
        sap.Invoices.Add(new Invoice { DocEntry = 2350001, DocNum = 778423, CardCode = Customer });
        // A different, perfectly good invoice number in Customer Ref. No. must lose to the copied lines.
        sap.Invoices.Add(new Invoice { DocEntry = 2350099, DocNum = 778500, CardCode = Customer });
        var memo = Memo(numAtCard: "778500", Copied(2350001), Copied(2350001, lineNum: 1), Manual(lineNum: 2));
        var fiscal = new RecordingFiscalisation();

        var outcome = await Fiscaliser(sap, fiscal).FiscaliseAsync(memo, Caller, CancellationToken.None);

        Assert.Null(outcome.Refusal);
        Assert.Equal("778423", Assert.Single(fiscal.Calls).OriginalInvoiceNumber);
        Assert.DoesNotContain(nameof(ISAPServiceLayerClient.GetInvoiceHeadersByDocNumAsync), sap.Calls);
    }

    [Fact]
    public async Task Lines_copied_from_two_invoices_are_refused_rather_than_filed_against_the_first()
    {
        var sap = new FakeSap();
        sap.Invoices.Add(new Invoice { DocEntry = 2350001, DocNum = 778423, CardCode = Customer });
        sap.Invoices.Add(new Invoice { DocEntry = 2350002, DocNum = 778424, CardCode = Customer });
        var memo = Memo(numAtCard: "778423", Copied(2350002), Copied(2350001, lineNum: 1));
        var fiscal = new RecordingFiscalisation();

        var outcome = await Fiscaliser(sap, fiscal).FiscaliseAsync(memo, Caller, CancellationToken.None);

        Assert.Empty(fiscal.Calls);
        Assert.Contains("more than one invoice (base DocEntries 2350001, 2350002)", outcome.Refusal);
        Assert.Contains("Split it", outcome.Refusal);
        Assert.Empty(sap.Calls);
        Assert.Contains("more than one invoice", Assert.Single(fiscal.Recorded).Request.Message);
    }

    [Fact]
    public async Task Lines_copied_from_something_other_than_an_invoice_are_refused()
    {
        var sap = new FakeSap();
        var memo = Memo(numAtCard: "778423", new SAPCreditNoteLine { LineNum = 0, ItemCode = "ITEM1", Quantity = 1, LineTotal = 10m, BaseType = 16, BaseEntry = 40001 });
        var fiscal = new RecordingFiscalisation();

        var outcome = await Fiscaliser(sap, fiscal).FiscaliseAsync(memo, Caller, CancellationToken.None);

        Assert.Empty(fiscal.Calls);
        Assert.Contains("base document type(s) 16", outcome.Refusal);
        Assert.Empty(sap.Calls);
    }

    [Fact]
    public async Task A_copied_invoice_SAP_cannot_return_is_refused()
    {
        var memo = Memo(numAtCard: null, Copied(2350001));
        var fiscal = new RecordingFiscalisation();

        var outcome = await Fiscaliser(new FakeSap(), fiscal).FiscaliseAsync(memo, Caller, CancellationToken.None);

        Assert.Empty(fiscal.Calls);
        Assert.Contains("invoice DocEntry 2350001 could not be read", outcome.Refusal);
    }

    // ── Nothing copied: Customer Ref. No. ────────────────────────────────────────

    [Fact]
    public async Task Nothing_copied_files_against_the_customers_one_live_invoice_named_in_Customer_Ref_No()
    {
        var sap = new FakeSap();
        // Cancelled, and another customer's: neither stops the live own invoice being the one.
        sap.Invoices.Add(new Invoice { DocEntry = 2350001, DocNum = 778423, CardCode = Customer, Cancelled = SapYesNo.Yes });
        sap.Invoices.Add(new Invoice { DocEntry = 2350002, DocNum = 778423, CardCode = OtherCustomer });
        sap.Invoices.Add(new Invoice { DocEntry = 2350003, DocNum = 778423, CardCode = Customer.ToLowerInvariant() + " ", Cancelled = SapYesNo.No });
        var memo = Memo(numAtCard: "  778423 ", Manual());
        var fiscal = new RecordingFiscalisation();

        var outcome = await Fiscaliser(sap, fiscal).FiscaliseAsync(memo, Caller, CancellationToken.None);

        Assert.Null(outcome.Refusal);
        Assert.Equal("778423", Assert.Single(fiscal.Calls).OriginalInvoiceNumber);
        Assert.Equal([nameof(ISAPServiceLayerClient.GetInvoiceHeadersByDocNumAsync)], sap.Calls);
        Assert.Equal(778423, Assert.Single(sap.DocNumReads));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("INV-778423")]
    [InlineData("778423A")]
    [InlineData("+778423")]
    [InlineData("-778423")]
    [InlineData("7784 23")]
    [InlineData("0")]
    [InlineData("000")]
    [InlineData("99999999999")]
    [InlineData("７７８４２３")] // full-width digits are digits to char.IsDigit, not to the platform
    public async Task Nothing_copied_with_no_invoice_number_in_Customer_Ref_No_is_refused_without_asking_SAP(string? numAtCard)
    {
        var sap = new FakeSap();
        var memo = Memo(numAtCard, Manual());
        var fiscal = new RecordingFiscalisation();

        var outcome = await Fiscaliser(sap, fiscal).FiscaliseAsync(memo, Caller, CancellationToken.None);

        Assert.Empty(fiscal.Calls);
        Assert.Empty(sap.Calls);
        Assert.Contains("not based on an invoice", outcome.Refusal);
        Assert.Contains("Customer Ref. No.", outcome.Refusal);
    }

    [Fact]
    public async Task Nothing_copied_naming_another_customers_invoice_is_refused()
    {
        var sap = new FakeSap();
        sap.Invoices.Add(new Invoice { DocEntry = 2350002, DocNum = 778423, CardCode = OtherCustomer });
        var fiscal = new RecordingFiscalisation();

        var outcome = await Fiscaliser(sap, fiscal).FiscaliseAsync(Memo("778423", Manual()), Caller, CancellationToken.None);

        Assert.Empty(fiscal.Calls);
        Assert.Contains($"belongs to {OtherCustomer}, not {Customer}", outcome.Refusal);
    }

    [Fact]
    public async Task Nothing_copied_naming_an_invoice_SAP_does_not_hold_live_is_refused()
    {
        var sap = new FakeSap();
        sap.Invoices.Add(new Invoice { DocEntry = 2350001, DocNum = 778423, CardCode = Customer, Cancelled = SapYesNo.Yes });
        var fiscal = new RecordingFiscalisation();

        var outcome = await Fiscaliser(sap, fiscal).FiscaliseAsync(Memo("778423", Manual()), Caller, CancellationToken.None);

        Assert.Empty(fiscal.Calls);
        Assert.Contains("SAP has no live A/R invoice with that number", outcome.Refusal);
    }

    [Fact]
    public async Task Nothing_copied_naming_a_number_two_of_the_customers_invoices_share_is_refused()
    {
        var sap = new FakeSap();
        sap.Invoices.Add(new Invoice { DocEntry = 2350001, DocNum = 778423, CardCode = Customer });
        sap.Invoices.Add(new Invoice { DocEntry = 9100001, DocNum = 778423, CardCode = Customer });
        var fiscal = new RecordingFiscalisation();

        var outcome = await Fiscaliser(sap, fiscal).FiscaliseAsync(Memo("778423", Manual()), Caller, CancellationToken.None);

        Assert.Empty(fiscal.Calls);
        Assert.Contains("2 A/R invoices with that number", outcome.Refusal);
    }

    // ── The pick itself ──────────────────────────────────────────────────────────

    [Fact]
    public void The_base_link_ignores_lines_with_no_base_and_counts_each_invoice_once()
    {
        var link = SapCreditNoteOriginalInvoice.BaseLinkOf(Memo(null, Copied(2350001), Manual(lineNum: 1), Copied(2350001, lineNum: 2)));
        Assert.Equal(2350001, link.SingleInvoiceDocEntry);

        var several = SapCreditNoteOriginalInvoice.BaseLinkOf(Memo(null, Copied(2350002), Copied(2350001, lineNum: 1)));
        Assert.Null(several.SingleInvoiceDocEntry);
        Assert.Equal([2350001, 2350002], several.InvoiceDocEntries);

        var none = SapCreditNoteOriginalInvoice.BaseLinkOf(new SAPCreditNote { DocumentLines = null });
        Assert.Null(none.SingleInvoiceDocEntry);
        Assert.Empty(none.InvoiceDocEntries);
        Assert.Empty(none.OtherBaseTypes);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private static readonly SapCreditNoteFiscalisationCaller Caller = new("Test", "TEST", "a test", CaptureIncidents: false);

    private SapCreditNoteFiscaliser Fiscaliser(FakeSap sap, RecordingFiscalisation fiscal) => new(
        _context,
        sap.AsClient(),
        fiscal.AsService(),
        fiscal.AsSender(),
        Options.Create(new FiscalisationSettings { Provider = FiscalisationProvider.Platform }),
        NullLogger<SapCreditNoteFiscaliser>.Instance);

    private static SAPCreditNote Memo(string? numAtCard, params SAPCreditNoteLine[] lines) => new()
    {
        DocEntry = 160200,
        DocNum = 60200,
        CardCode = Customer,
        CardName = "Test customer",
        NumAtCard = numAtCard,
        DocTotal = 10m,
        DocCurrency = "USD",
        Cancelled = SapYesNo.No,
        DocumentLines = [.. lines]
    };

    private static SAPCreditNoteLine Copied(int invoiceDocEntry, int lineNum = 0) =>
        new() { LineNum = lineNum, ItemCode = "ITEM1", Quantity = 1, LineTotal = 10m, BaseType = 13, BaseEntry = invoiceDocEntry };

    private static SAPCreditNoteLine Manual(int lineNum = 0) =>
        new() { LineNum = lineNum, ItemCode = "ITEM2", Quantity = 1, LineTotal = 10m, BaseType = -1, BaseEntry = null };

    /// <summary>SAP's invoices, read by DocEntry or by DocNum (every invoice with it, as SAP returns them).</summary>
    private sealed class FakeSap
    {
        public List<Invoice> Invoices { get; } = [];
        public List<string> Calls { get; } = [];
        public List<int> DocNumReads { get; } = [];

        public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        {
            Calls.Add(method.Name);
            switch (method.Name)
            {
                case nameof(ISAPServiceLayerClient.GetInvoiceByDocEntryAsync):
                    return Task.FromResult(Invoices.FirstOrDefault(invoice => invoice.DocEntry == (int)args![0]!));
                case nameof(ISAPServiceLayerClient.GetInvoiceHeadersByDocNumAsync):
                    var docNum = (int)args![0]!;
                    DocNumReads.Add(docNum);
                    return Task.FromResult(Invoices.Where(invoice => invoice.DocNum == docNum).ToList());
                default:
                    throw new InvalidOperationException($"{method.Name} was not expected.");
            }
        });
    }

    private sealed class RecordingFiscalisation
    {
        public List<(InvoiceDto Document, string OriginalInvoiceNumber)> Calls { get; } = [];
        public List<SyncFiscalTransactionCommand> Recorded { get; } = [];

        public IFiscalizationService AsService() => StubProxy.For<IFiscalizationService>((method, args) =>
        {
            if (method.Name != nameof(IFiscalizationService.FiscalizeCreditNoteAsync))
            {
                throw new InvalidOperationException($"{method.Name} was not expected.");
            }

            Calls.Add(((InvoiceDto)args![0]!, (string)args[1]!));
            return Task.FromResult(new FiscalizationResult { Success = true, Message = "ok" });
        });

        public ISender AsSender() => StubProxy.For<ISender>((method, args) =>
        {
            if (method.Name != nameof(ISender.Send) || args?[0] is not SyncFiscalTransactionCommand command)
            {
                throw new InvalidOperationException($"{method.Name} was not expected.");
            }

            Recorded.Add(command);
            return Task.FromResult<ErrorOr<FiscalTransactionLogItemDto>>(new FiscalTransactionLogItemDto());
        });
    }
}
