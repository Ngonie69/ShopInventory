using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopCreditNotes;

public sealed class DesktopCreditNoteService(ApplicationDbContext db, IDesktopCreditFiscalGateway fiscal,
    DesktopCreditSapPoster sapPoster, IAuditService audit, ILogger<DesktopCreditNoteService> logger)
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
        var source = await fiscal.ReadOriginalAsync(sale, ct);
        var notes = await db.DesktopCreditNotes.AsNoTracking().Where(n => n.SaleId == sale.Id).ToListAsync(ct);
        return new DesktopCreditForm(source, notes.Select(Map).ToList(), Reserved(notes));
    }

    public async Task<DesktopCreditNoteResult> CreateAsync(Guid caller, string reference,
        CreateDesktopCreditRequest request, CancellationToken ct)
    {
        var sale = await ReadSale(caller, reference, ct);
        RequireFiscalised(sale);
        if (!Guid.TryParseExact(request.RequestKey, "N", out _) || request.Lines is null)
            throw new InvalidOperationException("A valid request key and selected lines are required.");
        request = request with { Lines = request.Lines.OrderBy(l => l.LineNo).ToList() };
        var hash = IdempotencyRequestHash.Of(new { SaleId = sale.Id, Request = request });
        var previous = await db.DesktopCreditNotes.AsNoTracking().SingleOrDefaultAsync(n => n.RequestKey == request.RequestKey, ct);
        if (previous is not null)
        {
            if (previous.SaleId != sale.Id || previous.RequestHash != hash)
                throw new InvalidOperationException("This request key belongs to a different credit note. Reopen the form for a new credit.");
            return previous.Status == DesktopCreditStatuses.Prepared ? await IssueAsync(previous, ct) : Map(previous);
        }
        var source = await fiscal.ReadOriginalAsync(sale, ct);
        DesktopCreditNoteEntity note;
        // The reservation of quantities and amount is serializable across app instances. No HTTP
        // request runs inside this transaction; it only reserves the immutable credit plan.
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var notes = await db.DesktopCreditNotes.AsNoTracking().Where(n => n.SaleId == sale.Id).ToListAsync(ct);
            var plan = DesktopCreditPlanner.Build(source, request, Reserved(notes),
                notes.Where(n => n.Status != DesktopCreditStatuses.Rejected).Sum(n => n.Amount), DateTime.UtcNow);
            plan.Receipt.Username = caller.ToString();
            note = new DesktopCreditNoteEntity
            {
                Id = Guid.NewGuid(), SaleId = sale.Id, RequestKey = request.RequestKey, RequestHash = hash,
                Number = plan.Receipt.InvoiceNo!, OriginalFiscalNumber = source.OriginalFiscalNumber,
                Reason = request.Reason.Trim(), Currency = source.Currency, Amount = plan.Amount,
                PlanJson = JsonSerializer.Serialize(plan, Json), CreatedAtUtc = DateTime.UtcNow, CreatedBy = caller,
                Message = "Saved; fiscalisation has not yet been submitted."
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

    private static Dictionary<int, decimal> Reserved(IEnumerable<DesktopCreditNoteEntity> notes) => notes
        .Where(n => n.Status != DesktopCreditStatuses.Rejected)
        .SelectMany(n => JsonSerializer.Deserialize<DesktopCreditPlan>(n.PlanJson, Json)!.Quantities)
        .GroupBy(l => l.LineNo).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

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
