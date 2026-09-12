using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopCreditNotes;

/// <summary>
/// Raises the SAP credit memo for a credit ZIMRA has already accepted.
/// </summary>
/// <remarks>
/// <para>
/// The back-office half of <see cref="DesktopCreditNoteService"/>, which files the fiscal credit and
/// stops there. It stops there for a good reason — the customer is at the counter and the receipt in
/// their hand is the only document that certainly exists — but SAP still holds the invoice, and until
/// this runs the two systems disagree about a return.
/// </para>
/// <para>
/// <b>Only ever after ZIMRA.</b> Nothing here touches a credit whose <c>Status</c> is not
/// <c>Fiscalised</c>: a credit the device refused, or one whose outcome is unknown, must not become a
/// SAP document. That ordering is the opposite of every other document in this system, and it is
/// inherited from the sale itself, which is fiscalised before it posts.
/// </para>
/// <para>
/// <b>Deferred is the ordinary case, not a failure.</b> A sale credited at 11:00 has no SAP invoice to
/// credit until that evening. The credit waits as <see cref="DesktopCreditSapStatuses.Deferred"/> and
/// is raised the moment the sale posts — <c>DesktopSalePostingService</c> calls this directly, and
/// <see cref="DesktopCreditSapSweep"/> catches what that call cannot.
/// </para>
/// </remarks>
public sealed class DesktopCreditSapPoster(
    ApplicationDbContext db,
    ISAPServiceLayerClient sap,
    IStockLedger stockLedger,
    IAuditService audit,
    ILogger<DesktopCreditSapPoster> logger)
{
    /// <summary>
    /// Carries one credit as far towards SAP as it can go now. Safe to call on a credit that is
    /// already posted, or on one that has nothing owing.
    /// </summary>
    public async Task SettleAsync(Guid creditNoteId, CancellationToken cancellationToken)
    {
        var note = await db.DesktopCreditNotes
            .FirstOrDefaultAsync(n => n.Id == creditNoteId, cancellationToken);

        if (note is null)
        {
            return;
        }

        await SettleAsync(note, cancellationToken);
    }

    /// <summary>
    /// Carries every credit against one sale forward. What the posting service calls the moment a sale
    /// reaches SAP, which is when a deferred credit becomes raisable at all.
    /// </summary>
    public async Task SettleForSaleAsync(int saleId, CancellationToken cancellationToken)
    {
        var notes = await db.DesktopCreditNotes
            .Where(n => n.SaleId == saleId
                && n.Status == DesktopCreditStatuses.Fiscalised
                && (n.SapStatus == DesktopCreditSapStatuses.Deferred
                    || n.SapStatus == DesktopCreditSapStatuses.Failed))
            .OrderBy(n => n.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        foreach (var note in notes)
        {
            await SettleAsync(note, cancellationToken);
        }
    }

    private async Task SettleAsync(DesktopCreditNoteEntity note, CancellationToken cancellationToken)
    {
        if (note.Status != DesktopCreditStatuses.Fiscalised)
        {
            // ZIMRA has not accepted this credit. Nothing may be raised against it, and nothing is
            // recorded as owing either — the fiscal side decides whether this credit exists at all.
            return;
        }

        if (note.SapStatus is DesktopCreditSapStatuses.Posted
            or DesktopCreditSapStatuses.NotRequired
            or DesktopCreditSapStatuses.ManualInSap)
        {
            return;
        }

        var sale = await db.DesktopSales
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == note.SaleId, cancellationToken);

        if (sale is null)
        {
            return;
        }

        // The units come back as soon as ZIMRA has the credit, not when SAP does. They never left the
        // ledger through SAP in the first place — the sale deducted them locally when it was rung up —
        // and holding them until this evening would mean a shop refused sales all afternoon for goods
        // standing on its own counter.
        await ReturnUnitsToLedgerAsync(note, sale);

        var resolved = ResolveSapStatus(sale);

        if (resolved is DesktopCreditSapStatuses.NotRequired or DesktopCreditSapStatuses.ManualInSap)
        {
            note.SapStatus = resolved;
            note.SapError = null;
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        if (sale.SapDocEntry is not > 0)
        {
            // Still waiting for the sale to post. Not a failure and not an attempt — nothing was sent.
            return;
        }

        await PostAsync(note, sale, cancellationToken);
    }

    /// <remarks>
    /// Read from the sale on every pass rather than trusted from when the credit was raised: the whole
    /// point of a deferred credit is that the sale's SAP state changes underneath it.
    /// </remarks>
    private static string ResolveSapStatus(DesktopSaleEntity sale) => sale switch
    {
        // Its own invoice, already in SAP — creditable now. Read before the Consolidated arm below,
        // because a posted sale is both.
        { SapDocEntry: > 0 } => DesktopCreditSapStatuses.Deferred,

        { ConsolidationStatus: DesktopSaleConsolidationStatus.Excluded } =>
            DesktopCreditSapStatuses.NotRequired,

        { ConsolidationStatus: DesktopSaleConsolidationStatus.Consolidated } =>
            DesktopCreditSapStatuses.ManualInSap,

        _ => DesktopCreditSapStatuses.Deferred
    };

    private async Task PostAsync(
        DesktopCreditNoteEntity note,
        DesktopSaleEntity sale,
        CancellationToken cancellationToken)
    {
        note.SapReference ??= note.Number;

        // A post was issued and its outcome never reached us. SAP may hold the memo; ask before
        // sending anything, and treat an unanswerable question as "it may exist".
        if (note.SapPostIssuedAtUtc is not null)
        {
            var recovered = await TryFindPostedAsync(note);
            if (recovered is not null)
            {
                MarkPosted(note, recovered);
                await SaveAndAuditAsync(note, sale, "adopted");
                return;
            }
        }

        CreateCreditNoteRequest request;

        try
        {
            request = BuildRequest(note, sale);
        }
        catch (InvalidOperationException refusal)
        {
            // The credited lines cannot be tied back to the invoice's own lines with certainty. A
            // credit memo built on a guessed line credits the wrong item, so this stops and says so
            // rather than posting something plausible.
            note.SapStatus = DesktopCreditSapStatuses.ManualInSap;
            note.SapError = Truncate(refusal.Message, 2000);

            logger.LogError(
                "Credit {CreditNote} cannot be posted to SAP automatically: {Reason}",
                note.Number, refusal.Message);

            await SaveAndAuditAsync(note, sale, "handed to a person");
            return;
        }

        note.SapAttempts++;

        try
        {
            var alreadyPosted = await sap.GetCreditNoteByReferenceAsync(note.SapReference!, cancellationToken);

            if (alreadyPosted is not null)
            {
                logger.LogWarning(
                    "SAP already holds credit memo {DocEntry} under reference {Reference}; adopting it "
                    + "rather than posting again.",
                    alreadyPosted.DocEntry, note.SapReference);

                MarkPosted(note, alreadyPosted);
                await SaveAndAuditAsync(note, sale, "adopted");
                return;
            }

            // Committed before the request goes out. SapDocEntry is set from the reply, so a post that
            // commits in SAP and then loses its reply would otherwise leave no local trace at all and
            // the next pass would raise a second memo.
            note.SapPostIssuedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            var posted = await sap.CreateCreditNoteAsync(request, CancellationToken.None);

            MarkPosted(note, posted);

            logger.LogInformation(
                "Posted credit {CreditNote} to SAP as credit memo {DocNum}, against invoice {InvoiceDocNum}.",
                note.Number, posted.DocNum, sale.SapDocNum);

            await SaveAndAuditAsync(note, sale, "posted");
        }
        catch (Exception ex)
        {
            note.SapStatus = DesktopCreditSapStatuses.Failed;
            note.SapError = Truncate(ex.Message, 2000);

            // SAP answered, and the answer was no. Nothing was created, so the marker comes off or the
            // next pass would only ever ask about a document that will never appear.
            if (SapFailureClassifier.DefinitelyNotCommitted(ex))
            {
                note.SapPostIssuedAtUtc = null;
            }

            logger.LogError(ex, "Credit {CreditNote} was not posted to SAP.", note.Number);

            await SaveAndAuditAsync(note, sale, "refused by SAP");
        }
    }

    /// <summary>
    /// Builds the credit memo, based on the invoice the sale posted as.
    /// </summary>
    /// <remarks>
    /// <b>Based on the invoice, never standalone.</b> BaseType/BaseEntry/BaseLine are what let SAP take
    /// the batches from the document being credited: a batch-managed line with no batch selection is
    /// refused, and the whole document with it, and nothing here knows which batches the invoice was
    /// allocated.
    /// </remarks>
    private static CreateCreditNoteRequest BuildRequest(DesktopCreditNoteEntity note, DesktopSaleEntity sale)
    {
        var plan = JsonSerializer.Deserialize<DesktopCreditPlan>(note.PlanJson, DesktopCreditNoteService.Json)
            ?? throw new InvalidOperationException("The saved credit plan could not be read.");

        // The invoice's line order, which is the sale's own lines by LineNum — see
        // DesktopSaleInvoiceRequestBuilder. BaseLine is the index in that order.
        var invoiceLines = sale.Lines.OrderBy(line => line.LineNum).ToList();

        var lines = new List<CreateCreditNoteLineRequest>();

        foreach (var credited in plan.Quantities.OrderBy(q => q.LineNo))
        {
            var index = invoiceLines.FindIndex(line => line.LineNum == credited.LineNo);

            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"Receipt line {credited.LineNo} does not match any line on sale {sale.ExternalReferenceId}, "
                    + "so the SAP credit memo cannot name the invoice line it reverses.");
            }

            var saleLine = invoiceLines[index];

            // The receipt line the operator actually chose, checked against the sale line it is being
            // tied to. The two are numbered by the same value on the way out — REVMax is sent the
            // sale's LineNum as the line's HH — but this document credits real stock, so the match is
            // verified rather than assumed.
            var source = plan.Source.Lines.SingleOrDefault(line => line.LineNo == credited.LineNo);

            if (source is not null
                && !string.IsNullOrWhiteSpace(saleLine.ItemDescription)
                && !string.Equals(source.Name, saleLine.ItemDescription, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Receipt line {credited.LineNo} is \"{source.Name}\" but sale line {saleLine.LineNum} is "
                    + $"\"{saleLine.ItemDescription}\". The SAP credit memo would credit the wrong item.");
            }

            if (credited.Quantity > saleLine.Quantity)
            {
                throw new InvalidOperationException(
                    $"Receipt line {credited.LineNo} credits {credited.Quantity} of {saleLine.ItemCode}, but the "
                    + $"sale line holds {saleLine.Quantity}.");
            }

            lines.Add(new CreateCreditNoteLineRequest
            {
                ItemCode = saleLine.ItemCode,
                ItemDescription = saleLine.ItemDescription,
                Quantity = credited.Quantity,
                UnitPrice = saleLine.UnitPrice,
                DiscountPercent = saleLine.DiscountPercent,
                WarehouseCode = string.IsNullOrWhiteSpace(saleLine.WarehouseCode)
                    ? sale.WarehouseCode
                    : saleLine.WarehouseCode,
                ReturnReason = note.Reason,
                OriginalInvoiceLineId = index
            });
        }

        if (lines.Count == 0)
        {
            throw new InvalidOperationException("The saved credit plan names no lines.");
        }

        return new CreateCreditNoteRequest
        {
            CardCode = sale.CardCode,
            CardName = sale.CardName,
            Type = CreditNoteType.Return,
            OriginalInvoiceDocEntry = sale.SapDocEntry,
            OriginalInvoiceDocNum = sale.SapDocNum,
            Reason = note.Reason,
            Comments = $"Reverses till sale {sale.ExternalReferenceId} — fiscal credit {note.Number}.",
            Currency = sale.Currency,
            RestockItems = true,
            RestockWarehouseCode = sale.WarehouseCode,
            // Server-set, as on every other path: it is the only thing in SAP that identifies this memo
            // as this credit's, and a caller must never be able to name it.
            SapReference = note.SapReference,
            Lines = lines
        };
    }

    /// <remarks>
    /// Swallows its own failure and answers null, which leaves the credit owing and the marker
    /// standing — so the next pass asks again rather than posting again.
    /// </remarks>
    private async Task<SAPCreditNote?> TryFindPostedAsync(DesktopCreditNoteEntity note)
    {
        try
        {
            return await sap.GetCreditNoteByReferenceAsync(note.SapReference!, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Could not ask SAP whether credit {Reference} was already posted.", note.SapReference);
            return null;
        }
    }

    private static void MarkPosted(DesktopCreditNoteEntity note, SAPCreditNote posted)
    {
        note.SapDocEntry = posted.DocEntry;
        note.SapDocNum = posted.DocNum;
        note.SapStatus = DesktopCreditSapStatuses.Posted;
        note.SapPostedAt = DateTime.UtcNow;
        note.SapError = null;
    }

    /// <summary>
    /// Puts the credited units back on the shared stock ledger, once.
    /// </summary>
    /// <remarks>
    /// The ledger is the morning's snapshot less everything the system has promised since, and a return
    /// is the one thing that moves it the other way. Guarded by its own flag rather than by the SAP
    /// status, because it happens hours earlier and returning the same units twice invents stock.
    /// </remarks>
    private async Task ReturnUnitsToLedgerAsync(DesktopCreditNoteEntity note, DesktopSaleEntity sale)
    {
        if (note.UnitsReturnedToLedger)
        {
            return;
        }

        List<StockLedgerLine> returned;

        try
        {
            var plan = JsonSerializer.Deserialize<DesktopCreditPlan>(note.PlanJson, DesktopCreditNoteService.Json)!;
            var byLineNum = sale.Lines.ToDictionary(line => line.LineNum);

            returned = plan.Quantities
                .Where(credited => byLineNum.ContainsKey(credited.LineNo))
                .Select(credited =>
                {
                    var line = byLineNum[credited.LineNo];
                    return new StockLedgerLine(
                        line.ItemCode,
                        string.IsNullOrWhiteSpace(line.WarehouseCode) ? sale.WarehouseCode : line.WarehouseCode,
                        credited.Quantity);
                })
                .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode)
                            && !string.IsNullOrWhiteSpace(line.WarehouseCode)
                            && line.Quantity > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the credit plan for {CreditNote} to return its units.", note.Number);
            return;
        }

        if (returned.Count == 0)
        {
            return;
        }

        try
        {
            await stockLedger.ReleaseAsync(returned, $"credit note {note.Number}", CancellationToken.None);

            note.UnitsReturnedToLedger = true;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The credit is with ZIMRA and that is what matters. A ledger that did not take the units
            // back reads short until the morning fetch, which refuses sales rather than allowing them.
            logger.LogWarning(
                ex, "Credit {CreditNote} is fiscalised, but its units could not be returned to the ledger.",
                note.Number);
        }
    }

    /// <remarks>
    /// Audited whatever happened, and never allowed to fail the credit: by the time this runs the memo
    /// may be in SAP, and a failed audit write must not turn that into an error somebody retries.
    /// </remarks>
    private async Task SaveAndAuditAsync(DesktopCreditNoteEntity note, DesktopSaleEntity sale, string outcome)
    {
        await db.SaveChangesAsync(CancellationToken.None);

        try
        {
            await audit.LogAsync(
                AuditActions.PostDesktopCreditNoteToSAP,
                nameof(DesktopCreditNoteEntity),
                note.Number,
                $"Credit {note.Number} against sale {sale.ExternalReferenceId} {outcome}"
                + (note.SapDocNum is null ? "" : $" as credit memo {note.SapDocNum}"),
                note.SapStatus == DesktopCreditSapStatuses.Posted,
                note.SapError);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the SAP posting of credit {CreditNote}.", note.Number);
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
