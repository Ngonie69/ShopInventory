using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the back-office half of a desktop credit: the SAP credit memo that follows the fiscal one.
///
/// The ordering is what these are really about. A till sale is fiscalised when it is rung up and posts
/// to SAP hours later, so a credit taken at the counter is with ZIMRA before SAP has anything to
/// credit. It waits, and is raised the moment the sale posts — and it is never raised at all for a
/// credit ZIMRA did not accept.
/// </summary>
public sealed class DesktopCreditSapPostingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingSap _sap = new();
    private readonly RecordingLedger _ledger = new();

    public DesktopCreditSapPostingTests()
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

    // ── ZIMRA first, always ──────────────────────────────────────────────

    [Theory]
    [InlineData(DesktopCreditStatuses.Prepared)]
    [InlineData(DesktopCreditStatuses.Submitting)]
    [InlineData(DesktopCreditStatuses.Rejected)]
    [InlineData(DesktopCreditStatuses.ReconciliationRequired)]
    public async Task A_credit_zimra_has_not_accepted_never_reaches_sap(string status)
    {
        // The whole ordering rests on this. A credit the device refused — or one whose outcome nobody
        // has established — must not become a SAP document, however long it sits there.
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale, status: status);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);
        Assert.Empty(_ledger.Released);

        var settled = await Reload(note.Id);
        Assert.Equal(DesktopCreditSapStatuses.Deferred, settled.SapStatus);
        Assert.Null(settled.SapDocNum);
    }

    // ── The unposted sale: wait, then raise ──────────────────────────────

    [Fact]
    public async Task A_credit_against_a_sale_that_has_not_posted_waits_for_it()
    {
        var sale = await GivenSaleAsync();
        var note = await GivenCreditAsync(sale);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);

        var settled = await Reload(note.Id);
        Assert.Equal(DesktopCreditSapStatuses.Deferred, settled.SapStatus);

        // The units come back now regardless, because they never left the ledger through SAP — the
        // sale deducted them locally when it was rung up.
        Assert.Equal(2m, Assert.Single(_ledger.Released).Quantity);
        Assert.True(settled.UnitsReturnedToLedger);
    }

    [Fact]
    public async Task The_memo_is_raised_once_the_sale_posts()
    {
        var sale = await GivenSaleAsync();
        var note = await GivenCreditAsync(sale);

        await Poster().SettleAsync(note.Id, CancellationToken.None);
        Assert.Empty(_sap.Created);

        // That evening.
        sale.SapDocEntry = 4242;
        sale.SapDocNum = 772109;
        sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
        await _context.SaveChangesAsync();

        // What DesktopSalePostingService calls the moment the invoice exists.
        await Poster().SettleForSaleAsync(sale.Id, CancellationToken.None);

        var request = Assert.Single(_sap.Created);
        // Based on the invoice, never standalone: BaseEntry is what lets SAP take the batches from the
        // document being credited, and a batch-managed line with no selection fails the whole document.
        Assert.Equal(4242, request.OriginalInvoiceDocEntry);
        Assert.Equal("COR007", request.CardCode);

        var settled = await Reload(note.Id);
        Assert.Equal(DesktopCreditSapStatuses.Posted, settled.SapStatus);
        Assert.Equal(88001, settled.SapDocNum);

        // And the units were not returned a second time on the way through.
        Assert.Single(_ledger.Released);
    }

    [Fact]
    public async Task A_credit_against_a_sale_already_in_sap_is_raised_at_once()
    {
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Single(_sap.Created);
        Assert.Equal(DesktopCreditSapStatuses.Posted, (await Reload(note.Id)).SapStatus);
    }

    // ── Partial credits ──────────────────────────────────────────────────

    [Fact]
    public async Task Only_the_credited_line_and_quantity_reach_sap()
    {
        // A partial credit is the ordinary case here: the customer brought two of the six back. The
        // memo must name that line of the invoice and that quantity, not the whole sale.
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale, creditedLineNum: 1, quantity: 3m);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        var line = Assert.Single(Assert.Single(_sap.Created).Lines);
        Assert.Equal("ICS027", line.ItemCode);
        Assert.Equal(3m, line.Quantity);
        // BaseLine is the line's index in the invoice's own order, which is the sale's lines by LineNum.
        Assert.Equal(1, line.OriginalInvoiceLineId);
        Assert.Equal("KEFGRS", line.WarehouseCode);

        Assert.Equal(3m, Assert.Single(_ledger.Released).Quantity);
    }

    [Fact]
    public async Task A_receipt_line_that_cannot_be_tied_to_the_invoice_is_handed_to_a_person()
    {
        // A credit memo built on a guessed line credits the wrong item, and stock moves for it. Better
        // to stop and say so than to post something plausible.
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale, creditedLineNum: 47);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);

        var settled = await Reload(note.Id);
        Assert.Equal(DesktopCreditSapStatuses.ManualInSap, settled.SapStatus);
        Assert.Contains("does not match any line", settled.SapError);
    }

    [Fact]
    public async Task A_receipt_line_naming_a_different_item_is_handed_to_a_person()
    {
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale, receiptLineName: "Something else entirely");

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);
        Assert.Contains("credit the wrong item", (await Reload(note.Id)).SapError);
    }

    // ── Never twice ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_memo_sap_already_holds_is_adopted_rather_than_raised_again()
    {
        // An earlier attempt committed in SAP and lost its reply. The reference is the only thing in
        // SAP that identifies the document as this credit's, and it is what recovers it.
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale);

        _sap.ExistingByReference[note.Number] = new SAPCreditNote { DocEntry = 999, DocNum = 88999 };

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);

        var settled = await Reload(note.Id);
        Assert.Equal(DesktopCreditSapStatuses.Posted, settled.SapStatus);
        Assert.Equal(88999, settled.SapDocNum);
    }

    [Fact]
    public async Task Settling_a_posted_credit_again_does_nothing()
    {
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale);

        await Poster().SettleAsync(note.Id, CancellationToken.None);
        await Poster().SettleAsync(note.Id, CancellationToken.None);
        await Poster().SettleForSaleAsync(sale.Id, CancellationToken.None);

        Assert.Single(_sap.Created);
        Assert.Single(_ledger.Released);
    }

    // ── Sales SAP is owed nothing for ────────────────────────────────────

    [Fact]
    public async Task A_sale_held_back_from_posting_owes_sap_nothing()
    {
        var sale = await GivenSaleAsync();
        sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Excluded;
        await _context.SaveChangesAsync();
        var note = await GivenCreditAsync(sale);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);
        Assert.Equal(DesktopCreditSapStatuses.NotRequired, (await Reload(note.Id)).SapStatus);
    }

    [Fact]
    public async Task A_consolidated_sale_is_handed_to_a_person()
    {
        // A consolidated invoice stands for many sales, so there is no invoice of its own to credit,
        // and a standalone memo would have to choose batches with nothing to take them from. ZIMRA
        // already holds the credit, which is the half nothing else can do.
        var sale = await GivenSaleAsync();
        sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
        await _context.SaveChangesAsync();
        var note = await GivenCreditAsync(sale);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);
        Assert.Equal(DesktopCreditSapStatuses.ManualInSap, (await Reload(note.Id)).SapStatus);

        // Still restocked: the goods came back over the counter whatever SAP holds.
        Assert.Single(_ledger.Released);
    }

    // ── What the invoice list is told afterwards ─────────────────────────

    [Fact]
    public async Task A_posted_memo_reads_as_fiscalised_on_the_credit_note_list()
    {
        // The hazard PerSaleInvoiceRegistry closes for invoices. Every fiscal lookup downstream is
        // keyed on the SAP DocNum, and the credit receipt was filed under the credit's own DCN- number
        // — so without this the memo reads "Not Fiscalised" and invites a second, irreversible filing.
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        var creditNotes = new List<CreditNoteDto>
        {
            new() { SAPDocNum = (await Reload(note.Id)).SapDocNum },
            new() { SAPDocNum = 90909 }
        };

        await FiscalDocumentStatusProjector.EnrichCreditNotesAsync(
            _context, creditNotes, CancellationToken.None);

        Assert.True(creditNotes[0].IsFiscalized);
        Assert.Equal("Fiscalised", creditNotes[0].FiscalizationStatus);

        // A credit memo this system did not raise is left exactly as it was: the registry widens, it
        // never asserts on somebody else's document.
        Assert.NotEqual(true, creditNotes[1].IsFiscalized);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private DesktopCreditSapPoster Poster() => new(
        _context,
        _sap.Client,
        _ledger.Ledger,
        StubProxy.For<IAuditService>((method, _) => method.Name == nameof(IAuditService.LogAsync)
            ? Task.CompletedTask
            : throw new InvalidOperationException($"IAuditService.{method.Name} was not expected.")),
        NullLogger<DesktopCreditSapPoster>.Instance);

    private Task<DesktopCreditNoteEntity> Reload(Guid id) =>
        _context.DesktopCreditNotes.AsNoTracking().SingleAsync(n => n.Id == id);

    private async Task<DesktopSaleEntity> GivenSaleAsync(int? sapDocEntry = null, int? sapDocNum = null)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = "GRC-FAC-20260912-A1",
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = "COR007",
            CardName = "Corner Shop",
            DocDate = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc),
            TotalAmount = 100m,
            Currency = "USD",
            WarehouseCode = "KEFGRS",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            FiscalDayNo = "525",
            FiscalReceiptNumber = "216877",
            SapDocEntry = sapDocEntry,
            SapDocNum = sapDocNum,
            ConsolidationStatus = sapDocEntry is null
                ? DesktopSaleConsolidationStatus.Pending
                : DesktopSaleConsolidationStatus.Consolidated,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0, ItemCode = "ICS025", ItemDescription = "Blueberry",
                    Quantity = 6, UnitPrice = 10m, LineTotal = 60m, WarehouseCode = "KEFGRS"
                },
                new DesktopSaleLineEntity
                {
                    LineNum = 1, ItemCode = "ICS027", ItemDescription = "Banana",
                    Quantity = 4, UnitPrice = 10m, LineTotal = 40m, WarehouseCode = "KEFGRS"
                }
            ]
        };

        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        return sale;
    }

    /// <remarks>
    /// Built through the real <see cref="DesktopCreditPlan"/>, serialised exactly as the fiscal service
    /// stores it — the poster reads that JSON back, so a hand-rolled shape would test nothing.
    /// </remarks>
    private async Task<DesktopCreditNoteEntity> GivenCreditAsync(
        DesktopSaleEntity sale,
        string status = DesktopCreditStatuses.Fiscalised,
        int creditedLineNum = 0,
        decimal quantity = 2m,
        string? receiptLineName = null)
    {
        var saleLine = sale.Lines.FirstOrDefault(line => line.LineNum == creditedLineNum);

        var source = new DesktopCreditSource(
            sale.ExternalReferenceId, sale.Currency, sale.TotalAmount, 22862, 525, 216877, null,
            [
                new DesktopCreditLine(
                    creditedLineNum,
                    receiptLineName ?? saleLine?.ItemDescription ?? "Unknown",
                    saleLine?.Quantity ?? quantity,
                    10m, 7, 15.5m, "O01", null)
            ]);

        var plan = new DesktopCreditPlan(
            source,
            [new DesktopCreditQuantity(creditedLineNum, quantity)],
            new SubmitReceiptApiRequest { InvoiceNo = "DCN-test", ReceiptType = ReceiptType.CreditNote },
            quantity * 10m);

        var note = new DesktopCreditNoteEntity
        {
            Id = Guid.NewGuid(),
            SaleId = sale.Id,
            RequestKey = Guid.NewGuid().ToString("N"),
            RequestHash = "hash",
            Number = $"DCN-{Guid.NewGuid():N}",
            OriginalFiscalNumber = sale.ExternalReferenceId,
            Reason = "Customer return",
            Currency = sale.Currency,
            Amount = plan.Amount,
            Status = status,
            PlanJson = JsonSerializer.Serialize(plan, DesktopCreditNoteService.Json),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = Guid.NewGuid()
        };

        _context.DesktopCreditNotes.Add(note);
        await _context.SaveChangesAsync();
        _context.Entry(note).State = EntityState.Detached;
        return note;
    }

    private sealed class RecordingSap
    {
        private int _nextDocNum = 88001;

        public List<CreateCreditNoteRequest> Created { get; } = [];

        public Dictionary<string, SAPCreditNote> ExistingByReference { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) =>
            method.Name switch
            {
                // Cast to object so the switch's natural type is not taken from the arm below, which
                // answers a non-nullable Task — this lookup's contract is that it may answer null.
                nameof(ISAPServiceLayerClient.GetCreditNoteByReferenceAsync) =>
                    (object)Task.FromResult(
                        ExistingByReference.TryGetValue((string)args![0]!, out var found) ? found : null),

                nameof(ISAPServiceLayerClient.CreateCreditNoteAsync) =>
                    Create((CreateCreditNoteRequest)args![0]!),

                _ => throw new InvalidOperationException(
                    $"ISAPServiceLayerClient.{method.Name} was not expected on this path.")
            });

        private Task<SAPCreditNote> Create(CreateCreditNoteRequest request)
        {
            Created.Add(request);
            var docNum = _nextDocNum++;
            return Task.FromResult(new SAPCreditNote
            {
                DocEntry = docNum, DocNum = docNum, CardCode = request.CardCode, NumAtCard = request.SapReference
            });
        }
    }

    private sealed class RecordingLedger
    {
        public List<StockLedgerLine> Released { get; } = [];

        public IStockLedger Ledger => StubProxy.For<IStockLedger>((method, args) => method.Name switch
        {
            nameof(IStockLedger.ReleaseAsync) => Record((IReadOnlyList<StockLedgerLine>)args![0]!),
            _ => throw new InvalidOperationException($"IStockLedger.{method.Name} was not expected.")
        });

        private Task Record(IReadOnlyList<StockLedgerLine> lines)
        {
            Released.AddRange(lines);
            return Task.CompletedTask;
        }
    }
}
