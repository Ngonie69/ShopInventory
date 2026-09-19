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
        _sap.MirroringSalesFrom(_context);
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

    /// <summary>
    /// A van sale's receipt row is marked Consolidated before SAP has anything, and a credit against it
    /// is still owed a memo.
    /// </summary>
    /// <remarks>
    /// Consolidated on that row means "no posting route owns this document" — the reservation posts the
    /// invoice — and not "SAP has it inside a consolidated invoice". Read as the latter, every credit
    /// taken between the receipt and the invoice was written off as ManualInSap: nobody was told to
    /// raise it, and the sweep never looked at it again, so ZIMRA and SAP disagreed about the return
    /// permanently. The consolidation's own id is what distinguishes the two, and this row has none.
    /// </remarks>
    [Fact]
    public async Task A_credit_against_a_van_receipt_row_waits_for_the_invoice_rather_than_a_person()
    {
        var sale = await GivenSaleAsync();
        sale.SourceSystem = SaleSourceSystems.VanSalesOnline;
        sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
        sale.ConsolidationId = null;
        await _context.SaveChangesAsync();

        var note = await GivenCreditAsync(sale);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);
        Assert.Equal(DesktopCreditSapStatuses.Deferred, (await Reload(note.Id)).SapStatus);

        // The queue gets the invoice in, and the memo follows.
        sale.SapDocEntry = 4242;
        sale.SapDocNum = 772109;
        await _context.SaveChangesAsync();

        await Poster().SettleForSaleAsync(sale.Id, CancellationToken.None);

        Assert.Equal(4242, Assert.Single(_sap.Created).OriginalInvoiceDocEntry);
        Assert.Equal(DesktopCreditSapStatuses.Posted, (await Reload(note.Id)).SapStatus);
    }


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
        var note = await GivenCreditAsync(sale, receiptLineNo: 2, quantity: 3m);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        var line = Assert.Single(Assert.Single(_sap.Created).Lines);
        Assert.Equal("ICS027", line.ItemCode);
        Assert.Equal(3m, line.Quantity);
        // BaseLine is the line's index in the invoice's own order — see DesktopSaleLineOrder.
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
        var note = await GivenCreditAsync(sale, receiptLineNo: 47);

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

    /// <summary>
    /// INV1753, 19 September 2026: a till numbering its lines from zero was stored 1,1,2, and every
    /// credit against the sale threw "An item with the same key has already been added. Key: 1"
    /// instead of posting. Receipt lines are found by position, so a repeated LineNum costs nothing.
    /// </summary>
    [Fact]
    public async Task A_sale_with_a_repeated_line_number_still_credits_the_line_the_receipt_named()
    {
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 777347, lineNums: [1, 1, 2]);

        var first = await GivenCreditAsync(sale, receiptLineNo: 2, quantity: 1m);
        await Poster().SettleAsync(first.Id, CancellationToken.None);

        // The second credit replays the first one's batches, which is where the dictionary threw.
        var second = await GivenCreditAsync(sale, receiptLineNo: 3, quantity: 2m);
        await Poster().SettleAsync(second.Id, CancellationToken.None);

        Assert.Equal(DesktopCreditSapStatuses.Posted, (await Reload(first.Id)).SapStatus);
        Assert.Equal(DesktopCreditSapStatuses.Posted, (await Reload(second.Id)).SapStatus);

        var banana = Assert.Single(_sap.Created[0].Lines);
        Assert.Equal(("ICS027", 1, 1m), (banana.ItemCode, banana.OriginalInvoiceLineId!.Value, banana.Quantity));

        var cheese = Assert.Single(_sap.Created[1].Lines);
        Assert.Equal(("CHE011", 2, 2m), (cheese.ItemCode, cheese.OriginalInvoiceLineId!.Value, cheese.Quantity));

        Assert.Equal(["ICS027", "CHE011"], _ledger.Released.Select(l => l.ItemCode).ToArray());
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

    /// <summary>
    /// A memo whose reply was lost is not raised again while SAP's "no memo" cannot be trusted.
    /// </summary>
    /// <remarks>
    /// SAP can commit a document and not show it to a lookup for a few minutes. That lag invoiced five
    /// sales twice between 25 August and 8 September 2026; on a credit it would reverse one return
    /// twice. The memo waits out the same window the sale posting does.
    /// </remarks>
    [Fact]
    public async Task A_memo_whose_reply_was_lost_is_not_raised_again_inside_the_grace_window()
    {
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale);
        await GivenLostReplyAsync(note, ago: TimeSpan.FromMinutes(2));

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);

        var held = await Reload(note.Id);
        Assert.Equal(DesktopCreditSapStatuses.Failed, held.SapStatus);
        Assert.NotNull(held.SapPostIssuedAtUtc);
        // What the post failed with, not a note about the wait; and the wait spends nothing.
        Assert.Equal("The SAP Service Layer did not respond in time.", held.SapError);
        Assert.Equal(1, held.SapAttempts);
    }

    [Fact]
    public async Task A_memo_sap_shows_inside_the_grace_window_is_adopted()
    {
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale);
        await GivenLostReplyAsync(note, ago: TimeSpan.FromMinutes(2));

        _sap.ExistingByReference[note.Number] = new SAPCreditNote { DocEntry = 999, DocNum = 88999 };

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);
        Assert.Equal(88999, (await Reload(note.Id)).SapDocNum);
    }

    [Fact]
    public async Task A_memo_sap_still_does_not_show_after_the_window_is_raised()
    {
        // A post that genuinely never landed recovers on its own, just later.
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale);
        await GivenLostReplyAsync(note, ago: TimeSpan.FromMinutes(20));

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Single(_sap.Created);
        Assert.Equal(DesktopCreditSapStatuses.Posted, (await Reload(note.Id)).SapStatus);
    }

    [Fact]
    public async Task The_window_can_be_switched_off()
    {
        // The same switch the sale posting has, for an incident.
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 772109);
        var note = await GivenCreditAsync(sale);
        await GivenLostReplyAsync(note, ago: TimeSpan.FromMinutes(2));

        await Poster(graceMinutes: 0).SettleAsync(note.Id, CancellationToken.None);

        Assert.Single(_sap.Created);
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
        //
        // Seeded with the consolidation itself, as ConsolidateDailySalesHandler writes it: the id is
        // what says this sale is inside somebody else's invoice. The status alone does not — a van
        // sale's receipt row carries it from birth, with its own invoice still owed.
        var sale = await GivenSaleAsync();
        sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
        sale.ConsolidationId = await GivenConsolidationAsync(sale);
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

    // ── Batches ──────────────────────────────────────────────────────────

    /// <summary>
    /// INV1753, 19 September 2026: fiscalised, then refused by SAP with "Cannot add row without complete
    /// selection of batch/serial numbers". Basing the memo on the invoice does not give SAP the batches.
    /// </summary>
    [Fact]
    public async Task A_batch_managed_line_returns_into_the_batch_the_invoice_issued()
    {
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 777347);
        GivenInvoiceBatches(4242, line0: [("B-OLD", 6m)], line1: [("B-A", 1m), ("B-B", 3m)]);
        var note = await GivenCreditAsync(sale, receiptLineNo: 2, quantity: 2m);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        var line = Assert.Single(Assert.Single(_sap.Created).Lines);
        Assert.Equal(
            [("B-A", 1m), ("B-B", 1m)],
            line.BatchNumbers!.Select(b => (b.BatchNumber, b.Quantity)).ToArray());
        Assert.Equal(DesktopCreditSapStatuses.Posted, (await Reload(note.Id)).SapStatus);
    }

    [Fact]
    public async Task A_second_credit_on_the_same_line_skips_what_the_first_returned()
    {
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 777347);
        GivenInvoiceBatches(4242, line0: [("B-OLD", 6m)], line1: [("B-A", 1m), ("B-B", 3m)]);

        var first = await GivenCreditAsync(sale, receiptLineNo: 2, quantity: 2m);
        await Poster().SettleAsync(first.Id, CancellationToken.None);

        var second = await GivenCreditAsync(sale, receiptLineNo: 2, quantity: 2m);
        await Poster().SettleAsync(second.Id, CancellationToken.None);

        var line = Assert.Single(_sap.Created[1].Lines);
        Assert.Equal([("B-B", 2m)], line.BatchNumbers!.Select(b => (b.BatchNumber, b.Quantity)).ToArray());
    }

    [Fact]
    public async Task A_line_with_no_batches_on_the_invoice_names_none()
    {
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 777347);
        var note = await GivenCreditAsync(sale);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Null(Assert.Single(Assert.Single(_sap.Created).Lines).BatchNumbers);
    }

    [Fact]
    public async Task An_invoice_line_for_another_item_is_handed_to_a_person()
    {
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 777347);
        _sap.Invoices[4242] = new Invoice
        {
            DocEntry = 4242, DocNum = 777347,
            DocumentLines = [new InvoiceLine { LineNum = 0, ItemCode = "CHE011", Quantity = 6 }]
        };
        var note = await GivenCreditAsync(sale);

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);
        var settled = await Reload(note.Id);
        Assert.Equal(DesktopCreditSapStatuses.ManualInSap, settled.SapStatus);
        Assert.Contains("credit the wrong item", settled.SapError);
    }

    [Fact]
    public async Task An_invoice_sap_cannot_find_is_a_failed_attempt_and_sends_nothing()
    {
        var sale = await GivenSaleAsync(sapDocEntry: 4242, sapDocNum: 777347);
        var note = await GivenCreditAsync(sale);
        await _context.DesktopSales.Where(s => s.Id == sale.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SapDocEntry, 9999));

        await Poster().SettleAsync(note.Id, CancellationToken.None);

        Assert.Empty(_sap.Created);
        var settled = await Reload(note.Id);
        Assert.Equal(DesktopCreditSapStatuses.Failed, settled.SapStatus);
        Assert.Equal(1, settled.SapAttempts);
        Assert.Null(settled.SapPostIssuedAtUtc);
        Assert.Contains("to take its batches", settled.SapError);
    }

    [Fact]
    public void Batches_that_cannot_cover_the_return_refuse_rather_than_guess()
    {
        var request = new CreateCreditNoteRequest
        {
            CardCode = "COR007",
            Lines = [new CreateCreditNoteLineRequest { ItemCode = "ICS027", Quantity = 3m, OriginalInvoiceLineId = 0 }]
        };
        var invoice = new Invoice
        {
            DocNum = 777347,
            DocumentLines =
            [
                new InvoiceLine
                {
                    LineNum = 0, ItemCode = "ICS027", Quantity = 4,
                    BatchNumbers = [new InvoiceLineBatch { BatchNumber = "B-A", Quantity = 4m }]
                }
            ]
        };

        var refusal = Assert.Throws<InvalidOperationException>(
            () => CreditMemoBatchSelection.Apply(request, invoice, [(0, 2m)]));
        Assert.Contains("too little", refusal.Message);
    }

    private void GivenInvoiceBatches(
        int docEntry, (string Batch, decimal Qty)[] line0, (string Batch, decimal Qty)[] line1)
    {
        InvoiceLine Line(int num, string item, (string Batch, decimal Qty)[] batches) => new()
        {
            LineNum = num,
            ItemCode = item,
            Quantity = batches.Sum(b => b.Qty),
            BatchNumbers = batches.Select(b => new InvoiceLineBatch { BatchNumber = b.Batch, Quantity = b.Qty }).ToList()
        };

        _sap.Invoices[docEntry] = new Invoice
        {
            DocEntry = docEntry,
            DocNum = 777347,
            DocumentLines = [Line(0, "ICS025", line0), Line(1, "ICS027", line1)]
        };
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private DesktopCreditSapPoster Poster(int graceMinutes = 15) => new(
        _context,
        _sap.Client,
        _ledger.Ledger,
        StubProxy.For<IAuditService>((method, _) => method.Name == nameof(IAuditService.LogAsync)
            ? Task.CompletedTask
            : throw new InvalidOperationException($"IAuditService.{method.Name} was not expected.")),
        Microsoft.Extensions.Options.Options.Create(
            new ShopInventory.Configuration.DesktopSalePostingSettings { UnresolvedPostGraceMinutes = graceMinutes }),
        NullLogger<DesktopCreditSapPoster>.Instance);

    /// <summary>
    /// What a memo looks like after a post that went out and never answered: the marker set, the
    /// failure recorded, one attempt spent.
    /// </summary>
    private async Task GivenLostReplyAsync(DesktopCreditNoteEntity note, TimeSpan ago)
    {
        var tracked = await _context.DesktopCreditNotes.SingleAsync(n => n.Id == note.Id);
        tracked.SapReference = tracked.Number;
        tracked.SapPostIssuedAtUtc = DateTime.UtcNow - ago;
        tracked.SapStatus = DesktopCreditSapStatuses.Failed;
        tracked.SapError = "The SAP Service Layer did not respond in time.";
        tracked.SapAttempts = 1;
        await _context.SaveChangesAsync();
        _context.Entry(tracked).State = EntityState.Detached;
    }

    private Task<DesktopCreditNoteEntity> Reload(Guid id) =>
        _context.DesktopCreditNotes.AsNoTracking().SingleAsync(n => n.Id == id);

    private async Task<DesktopSaleEntity> GivenSaleAsync(
        int? sapDocEntry = null, int? sapDocNum = null, int[]? lineNums = null)
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

        if (lineNums is not null)
        {
            sale.Lines.Add(new DesktopSaleLineEntity
            {
                ItemCode = "CHE011", ItemDescription = "Cheddar",
                Quantity = 3, UnitPrice = 10m, LineTotal = 30m, WarehouseCode = "KEFGRS"
            });

            foreach (var (line, num) in sale.Lines.Zip(lineNums))
            {
                line.LineNum = num;
            }
        }

        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        return sale;
    }

    /// <summary>The end-of-day invoice a till sale is folded into.</summary>
    private async Task<int> GivenConsolidationAsync(DesktopSaleEntity sale)
    {
        var consolidation = new SaleConsolidationEntity
        {
            CardCode = sale.CardCode,
            ConsolidationDate = sale.DocDate,
            SaleCount = 4,
            TotalAmount = 400m,
            Status = ConsolidationStatus.Posted,
            SapDocEntry = 91000,
            SapDocNum = 773000
        };

        _context.SaleConsolidations.Add(consolidation);
        await _context.SaveChangesAsync();
        return consolidation.Id;
    }

    private Task<DesktopCreditNoteEntity> GivenCreditAsync(
        DesktopSaleEntity sale,
        string status = DesktopCreditStatuses.Fiscalised,
        int receiptLineNo = 1,
        decimal quantity = 2m,
        string? receiptLineName = null) =>
        DesktopCreditPosters.GivenCreditAsync(
            _context, sale, status, receiptLineNo, quantity, receiptLineName);
}
