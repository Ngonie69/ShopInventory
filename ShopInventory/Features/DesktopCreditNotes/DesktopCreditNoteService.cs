using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopCreditNotes;

public sealed class DesktopCreditNoteService(ApplicationDbContext db, IDesktopCreditFiscalGateway fiscal,
    DesktopCreditSapPoster sapPoster, IAuditService audit,
    IOptions<FiscalisationSettings> fiscalisationSettings, ILogger<DesktopCreditNoteService> logger)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<DesktopSaleEntity> ReadSale(Guid callerId, string reference, CancellationToken ct)
    {
        var caller = await db.Users.AsNoTracking().Include(u => u.Shop).SingleOrDefaultAsync(u => u.Id == callerId, ct);
        var scope = DesktopSalesReadScopeResolver.Resolve(caller);
        if (scope.IsError) throw new UnauthorizedAccessException("This account cannot access desktop sales.");
        var sale = await db.DesktopSales.AsNoTracking().SingleOrDefaultAsync(s => s.ExternalReferenceId == reference, ct)
            ?? throw new InvalidOperationException("The original sale was not found.");
        if (scope.Value.Narrow(sale.WarehouseCode).IsError)
            throw new UnauthorizedAccessException("This sale belongs to another warehouse.");
        return sale;
    }

    public async Task<List<DesktopCreditNoteResult>> ListAsync(Guid caller, string reference, CancellationToken ct)
    {
        var sale = await ReadSale(caller, reference, ct);
        var notes = await db.DesktopCreditNotes.AsNoTracking().Where(n => n.SaleId == sale.Id)
            .OrderByDescending(n => n.CreatedAtUtc).ToListAsync(ct);
        return notes.Select(Map).ToList();
    }

    public async Task<DesktopCreditForm> PrepareAsync(Guid caller, string reference, CancellationToken ct)
    {
        var sale = await ReadSale(caller, reference, ct);
        RequireFiscalised(sale);
        RequireCreditableHere(sale);
        var source = await fiscal.ReadOriginalAsync(sale, ct);
        var notes = await db.DesktopCreditNotes.AsNoTracking().Where(n => n.SaleId == sale.Id).ToListAsync(ct);
        return new DesktopCreditForm(source, notes.Select(Map).ToList(), Reserved(notes),
            InSap(sale), InSap(sale) ? sale.SapDocNum : null,
            Math.Max(0m, source.OriginalTotal - source.ExternalCreditedAmount - ReservedAmount(notes)));
    }

    /// <summary>The sale has its own SAP invoice, so a credit memo can be raised against it now.</summary>
    /// <remarks>
    /// The same test <see cref="DesktopCreditSapPoster"/> posts on. A sale inside a consolidated invoice
    /// has none of its own, so it is not "in SAP" here: there is nothing to post a memo against.
    /// </remarks>
    private static bool InSap(DesktopSaleEntity sale) => sale.SapDocEntry is > 0;

    /// <summary>Refuses the action the sale's SAP state does not allow, or that the form did not offer.</summary>
    /// <returns>Whether this is a fiscal-only credit against a sale already in SAP.</returns>
    private static bool RequireAction(DesktopSaleEntity sale, CreateDesktopCreditRequest request)
    {
        if (request.PostToSap == true && !InSap(sale))
            throw new InvalidOperationException("This sale is not in SAP yet, so there is no invoice to post a credit "
                + "memo against. Use Fiscalise only; the SAP credit memo follows on its own once the sale posts.");
        if (request.PostToSap != false || !InSap(sale)) return false;
        // "Fiscalise only" on a form opened before the sale posted meant "the memo follows later". Taken
        // now it would mean "SAP never hears of this", which nobody chose.
        if (request.SaleInSap != true)
            throw new InvalidOperationException($"This sale has posted to SAP as invoice {sale.SapDocNum} since the "
                + "form was opened. Refresh, then choose again.");
        return true;
    }

    /// <remarks>
    /// Hashed in the shape the request had before it carried a note or an action, whenever it carries no
    /// note, so a key saved by an earlier build still replays. The action is left out entirely: it is
    /// checked against the sale, not part of what the credit is.
    /// </remarks>
    private static string Hash(int saleId, CreateDesktopCreditRequest request) =>
        string.IsNullOrWhiteSpace(request.Note)
            ? IdempotencyRequestHash.Of(new { SaleId = saleId, Request = new { request.RequestKey, request.Reason, request.Lines } })
            : IdempotencyRequestHash.Of(new { SaleId = saleId, Request = new { request.RequestKey, request.Reason, request.Lines, Note = request.Note.Trim() } });

    public async Task<DesktopCreditNoteResult> CreateAsync(Guid caller, string reference,
        CreateDesktopCreditRequest request, CancellationToken ct)
    {
        var sale = await ReadSale(caller, reference, ct);
        RequireFiscalised(sale);
        RequireCreditableHere(sale);
        if (!Guid.TryParseExact(request.RequestKey, "N", out _) || request.Lines is null)
            throw new InvalidOperationException("A valid request key and selected lines are required.");
        request = request with { Lines = request.Lines.OrderBy(l => l.LineNo).ToList() };
        var hash = Hash(sale.Id, request);
        var previous = await db.DesktopCreditNotes.AsNoTracking().SingleOrDefaultAsync(n => n.RequestKey == request.RequestKey, ct);
        if (previous is not null)
        {
            if (previous.SaleId != sale.Id || previous.RequestHash != hash)
                throw new InvalidOperationException("This request key belongs to a different credit note. Reopen the form for a new credit.");
            return previous.Status == DesktopCreditStatuses.Prepared ? await IssueAsync(previous, ct) : Map(previous);
        }
        // After the replay above, not before: a credit saved by "Fiscalise only" whose sale has since
        // posted is the same credit, and its retry must return it rather than be refused.
        var fiscalOnly = RequireAction(sale, request);
        var source = await fiscal.ReadOriginalAsync(sale, ct);
        DesktopCreditNoteEntity note;
        // The reservation of quantities and amount is serializable across app instances. No HTTP
        // request runs inside this transaction; it only reserves the immutable credit plan.
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var notes = await db.DesktopCreditNotes.AsNoTracking().Where(n => n.SaleId == sale.Id).ToListAsync(ct);
            var plan = DesktopCreditPlanner.Build(source, request, Reserved(notes), ReservedAmount(notes), DateTime.UtcNow);
            plan.Receipt.Username = caller.ToString();
            note = new DesktopCreditNoteEntity
            {
                Id = Guid.NewGuid(), SaleId = sale.Id, RequestKey = request.RequestKey, RequestHash = hash,
                Number = plan.Receipt.InvoiceNo!, OriginalFiscalNumber = source.OriginalFiscalNumber,
                Reason = request.Reason.Trim(), Currency = source.Currency, Amount = plan.Amount,
                PlanJson = JsonSerializer.Serialize(plan, Json), CreatedAtUtc = DateTime.UtcNow, CreatedBy = caller,
                Message = "Saved; fiscalisation has not yet been submitted.",
                SapStatus = fiscalOnly ? DesktopCreditSapStatuses.FiscalOnly : DesktopCreditSapStatuses.Deferred
            };
            db.DesktopCreditNotes.Add(note);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        return await IssueAsync(note, ct);
    }

    private async Task<DesktopCreditNoteResult> IssueAsync(DesktopCreditNoteEntity note, CancellationToken ct)
    {
        var claimed = await db.DesktopCreditNotes.Where(n => n.Id == note.Id && n.Status == DesktopCreditStatuses.Prepared)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, DesktopCreditStatuses.Submitting)
                .SetProperty(n => n.SubmitStartedAtUtc, DateTime.UtcNow)
                .SetProperty(n => n.Message, "Submission is in progress. Check this saved note; do not create it again."), ct);
        if (claimed == 0) return await Reload(note.Id, ct);
        var plan = JsonSerializer.Deserialize<DesktopCreditPlan>(note.PlanJson, Json)!;
        var submitted = false;
        try
        {
            // Once claimed, disconnects cannot cancel either the submission or its durable outcome.
            var existing = await fiscal.FindAsync(plan, CancellationToken.None);
            if (existing?.Success == true && !existing.Skipped)
                return await SaveOutcome(note.Id, DesktopCreditStatuses.Fiscalised, existing, existing.Message);
            var refusal = await fiscal.PreflightAsync(plan, CancellationToken.None);
            if (refusal is not null) return await SaveOutcome(note.Id, DesktopCreditStatuses.Rejected, null, refusal);
            submitted = true;
            var outcome = await fiscal.SubmitAsync(plan, CancellationToken.None);
            var status = Outcome(outcome);
            return await SaveOutcome(note.Id, status, outcome, status == DesktopCreditStatuses.Rejected
                ? $"{outcome.Message ?? "The device refused the credit."} Nothing was filed, so this credit can be created again."
                : outcome.Message ?? "The fiscal outcome needs to be checked.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Desktop credit {CreditNote} could not complete", note.Number);
            // A failed preliminary lookup does not prove absence; retain the reservation in either case.
            return await SaveOutcome(note.Id, DesktopCreditStatuses.ReconciliationRequired, null,
                submitted ? "The submission outcome is unknown. Check the saved note before any further credit."
                          : "The fiscal service could not verify this credit. Check the saved note before any further credit.");
        }
    }

    /// <summary>What a completed submission proved, which is not always "somebody must go and look".</summary>
    /// <remarks>
    /// A refusal the device itself answered with - see <see cref="DeviceRefused"/> - proves nothing
    /// was filed. Recording that as <see cref="DesktopCreditStatuses.ReconciliationRequired"/>
    /// stranded the credit: no path resubmits that status, and its reservation is held against the
    /// original receipt for good, so a payload the device would never have accepted cost the customer
    /// the credit as well. DCN-4c96c570 died exactly that way, on an ITEMNAME2 sent empty.
    ///
    /// Everything else still stays put. A second fiscal receipt cannot be withdrawn, and that is the
    /// one mistake worth holding a reservation indefinitely to avoid.
    /// </remarks>
    private static string Outcome(FiscalizationResult outcome) =>
        outcome.Success && !outcome.Skipped ? DesktopCreditStatuses.Fiscalised
        : DeviceRefused(outcome) ? DesktopCreditStatuses.Rejected
        : DesktopCreditStatuses.ReconciliationRequired;

    /// <summary>The device answered, and its answer was no. Nothing was filed under this number.</summary>
    /// <remarks>
    /// Not the same thing as a call that failed. <see cref="RevmaxFiscalizationService.UnavailableErrorCode"/>
    /// is the outcome where the POST never came back and a lookup then found nothing: REVMax's own
    /// code calls that safe to retry, but a receipt filed a moment earlier that the lookup cannot yet
    /// see would make it a lie, and the retry files its credit under a fresh number no duplicate
    /// guard would catch. A refusal has no such window - the device read the request and declined it.
    /// </remarks>
    private static bool DeviceRefused(FiscalizationResult outcome) =>
        !outcome.Success && !outcome.Skipped && !outcome.RequiresReconciliation
        && outcome.ErrorCode != RevmaxFiscalizationService.UnavailableErrorCode;

    public async Task<DesktopCreditNoteResult> ReconcileAsync(Guid caller, string reference, Guid id, CancellationToken ct)
    {
        var sale = await ReadSale(caller, reference, ct);
        var note = await db.DesktopCreditNotes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id && n.SaleId == sale.Id, ct)
            ?? throw new InvalidOperationException("Credit note not found for this sale.");
        if (note.Status is DesktopCreditStatuses.Fiscalised or DesktopCreditStatuses.Rejected) return Map(note);
        var plan = JsonSerializer.Deserialize<DesktopCreditPlan>(note.PlanJson, Json)!;
        var result = await fiscal.FindAsync(plan, ct);
        if (result?.Success == true && !result.Skipped)
            return await SaveOutcome(note.Id, DesktopCreditStatuses.Fiscalised, result, "The existing fiscal receipt was found. No new receipt was submitted.");
        // Releases a credit stranded before Outcome() above told a refusal from an unknown outcome.
        // Its own stored result is the evidence and the only evidence used: a refusal the device
        // answered with proves it filed nothing, whatever the lookup says now. Anything short of that
        // is never released - a receipt filed a moment ago that the lookup cannot yet see would give
        // back a reservation against a receipt that exists, and a second credit receipt cannot be
        // withdrawn. Restricted to a note settled at ReconciliationRequired: Submitting may have a
        // POST in flight, and Prepared was never sent.
        if (result is null && note.Status == DesktopCreditStatuses.ReconciliationRequired
            && Stored(note) is { } stored && DeviceRefused(stored))
            return await SaveOutcome(note.Id, DesktopCreditStatuses.Rejected, null,
                $"{stored.Message ?? "The device refused the credit."} REVMax holds no receipt under this number, "
                + "so nothing was filed. The credit was released and can be created again.");
        return Map(note) with { Message = "No receipt was confirmed. The credit remains reserved for reconciliation; nothing was resubmitted." };
    }

    public async Task<DesktopCreditNoteResult> ContinueAsync(Guid caller, string reference, Guid id, CancellationToken ct)
    {
        var sale = await ReadSale(caller, reference, ct);
        var note = await db.DesktopCreditNotes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id && n.SaleId == sale.Id, ct)
            ?? throw new InvalidOperationException("Credit note not found for this sale.");
        return note.Status == DesktopCreditStatuses.Prepared ? await IssueAsync(note, ct) : Map(note);
    }

    private async Task<DesktopCreditNoteResult> SaveOutcome(Guid id, string status, FiscalizationResult? result, string? message)
    {
        var json = result is null ? null : JsonSerializer.Serialize(result, Json);
        DateTime? fiscalisedAt = status == DesktopCreditStatuses.Fiscalised ? DateTime.UtcNow : null;
        await db.DesktopCreditNotes.Where(n => n.Id == id && n.Status != DesktopCreditStatuses.Fiscalised)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, status).SetProperty(n => n.Message, message)
                // Kept when this outcome carries none of its own: a release records that the device
                // holds nothing, and erasing the refusal it answered with would take the only record
                // of why the credit failed with it.
                .SetProperty(n => n.FiscalResultJson, n => json ?? n.FiscalResultJson)
                .SetProperty(n => n.FiscalisedAtUtc, fiscalisedAt), CancellationToken.None);
        try { await audit.LogAsync("DesktopCreditNote", "DesktopCreditNote", id.ToString(), message, status == DesktopCreditStatuses.Fiscalised); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not audit desktop credit {Id}", id); }
        // ZIMRA has it; now the back office. Deferred when the sale has not posted yet, which is the
        // ordinary case at a till — see DesktopCreditSapPoster. Never allowed to disturb the fiscal
        // outcome above: the receipt is filed either way, and a SAP failure here is recorded on the
        // credit's own row and retried by the sweep rather than reported as a fiscal problem.
        if (status == DesktopCreditStatuses.Fiscalised)
        {
            try { await sapPoster.SettleAsync(id, CancellationToken.None); }
            catch (Exception ex) { logger.LogError(ex, "Could not raise the SAP credit memo for desktop credit {Id}", id); }
        }
        return await Reload(id, CancellationToken.None);
    }

    private async Task<DesktopCreditNoteResult> Reload(Guid id, CancellationToken ct) =>
        Map(await db.DesktopCreditNotes.AsNoTracking().SingleAsync(n => n.Id == id, ct));

    private static void RequireFiscalised(DesktopSaleEntity sale)
    {
        if (sale.FiscalizationStatus != DesktopSaleFiscalizationStatus.Success)
            throw new InvalidOperationException("The original sale must have a fiscal receipt before it can be credited.");
    }

    /// <summary>
    /// Refuses the sales this dialog cannot credit, saying which, before anything is prepared.
    /// </summary>
    /// <remarks>
    /// Both of these already failed — deep inside <c>RevmaxDesktopCreditGateway</c>, several reads in,
    /// with a message about REVMax returning a different invoice or not being enabled. That is a true
    /// statement about the plumbing and no help at all to the person holding the goods, who needs to be
    /// told either where to go instead or that nobody can do this today.
    /// </remarks>
    private void RequireCreditableHere(DesktopSaleEntity sale)
    {
        // Whoever holds the receipt is the only one who can credit it, and on a van sale that is not
        // settled by the source system. An online van row is a receipt carrier either way, but there
        // are two ways it got its receipt:
        //
        //  • This server signed it, before the invoice went anywhere — VanSaleFiscalFirstPoster calls
        //    FiscalizePreSapInvoiceAsync under the reservation's own reference, the same call the till
        //    uses. REVMax then holds the receipt under BuildPreSapInvoiceNo(reference), which is
        //    exactly the number the gateway below asks for, so this is creditable here and a sale
        //    whose invoice SAP has not taken is creditable here too — that is the whole point of
        //    signing first.
        //  • A handset signed it, off its own device's chain, and the row carries the signature so it
        //    can be handed to the platform. A device has one chain with one writer, so nothing here
        //    can sign a credit onto it; ReceiptIngestStatus is what says so, and it says so per row
        //    rather than per provider — a row signed under the platform outlives the switch back.
        if (sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.NotApplicable)
        {
            throw new InvalidOperationException(
                "This sale's receipt was signed on the handset's own fiscal device, so the credit has to "
                + "be filed on that device's chain and nothing on this server can write to it. Credit it "
                + "on the handset.");
        }

        // No platform credit gateway exists — Program.cs registers only the REVMax one. Under the
        // platform a van sale's receipt is signed on the handset's own chain, and a device has one
        // chain with one writer, so this server cannot sign a credit onto it however the code is
        // arranged. Said plainly rather than as "REVMax must be enabled", which reads like a setting
        // somebody could go and switch on.
        if (fiscalisationSettings.Value.UsesPlatform)
        {
            throw new InvalidOperationException(
                "Credit notes are filed on the REVMax device, and this server is set to the in-house "
                + "fiscalisation platform. Nothing here can credit a receipt while that is so — raise it "
                + "on the device.");
        }
    }

    private static Dictionary<int, decimal> Reserved(IEnumerable<DesktopCreditNoteEntity> notes) => notes
        .Where(n => n.Status != DesktopCreditStatuses.Rejected)
        .SelectMany(n => JsonSerializer.Deserialize<DesktopCreditPlan>(n.PlanJson, Json)!.Quantities)
        .GroupBy(l => l.LineNo).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

    private static decimal ReservedAmount(IEnumerable<DesktopCreditNoteEntity> notes) =>
        notes.Where(n => n.Status != DesktopCreditStatuses.Rejected).Sum(n => n.Amount);

    /// <summary>What the device last answered for this credit, as it was recorded.</summary>
    private static FiscalizationResult? Stored(DesktopCreditNoteEntity note) =>
        note.FiscalResultJson is null ? null : JsonSerializer.Deserialize<FiscalizationResult>(note.FiscalResultJson, Json);

    private static DesktopCreditNoteResult Map(DesktopCreditNoteEntity note)
    {
        var result = Stored(note);
        return new(note.Id, note.Number, note.Status, note.Amount, note.Currency, note.Reason,
            note.OriginalFiscalNumber, note.CreatedAtUtc, note.Message, result?.QRCode, result?.ReceiptGlobalNo,
            note.SapDocNum, note.SapStatus, note.SapError);
    }
}
