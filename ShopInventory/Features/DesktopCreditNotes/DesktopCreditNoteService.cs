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
    IAuditService audit, ILogger<DesktopCreditNoteService> logger)
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
            return previous.Status == "Prepared" ? await IssueAsync(previous, ct) : Map(previous);
        }
        var source = await fiscal.ReadOriginalAsync(sale, ct);
        DesktopCreditNoteEntity note;
        // The reservation of quantities and amount is serializable across app instances. No HTTP
        // request runs inside this transaction; it only reserves the immutable credit plan.
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var notes = await db.DesktopCreditNotes.AsNoTracking().Where(n => n.SaleId == sale.Id).ToListAsync(ct);
            var plan = DesktopCreditPlanner.Build(source, request, Reserved(notes),
                notes.Where(n => n.Status != "Rejected").Sum(n => n.Amount), DateTime.UtcNow);
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
        var claimed = await db.DesktopCreditNotes.Where(n => n.Id == note.Id && n.Status == "Prepared")
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, "Submitting")
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
                return await SaveOutcome(note.Id, "Fiscalised", existing, existing.Message);
            var refusal = await fiscal.PreflightAsync(plan, CancellationToken.None);
            if (refusal is not null) return await SaveOutcome(note.Id, "Rejected", null, refusal);
            submitted = true;
            var outcome = await fiscal.SubmitAsync(plan, CancellationToken.None);
            return await SaveOutcome(note.Id, outcome.Success && !outcome.Skipped ? "Fiscalised" : "ReconciliationRequired",
                outcome, outcome.Message ?? "The fiscal outcome needs to be checked.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Desktop credit {CreditNote} could not complete", note.Number);
            // A failed preliminary lookup does not prove absence; retain the reservation in either case.
            return await SaveOutcome(note.Id, "ReconciliationRequired", null,
                submitted ? "The submission outcome is unknown. Check the saved note before any further credit."
                          : "The fiscal service could not verify this credit. Check the saved note before any further credit.");
        }
    }

    public async Task<DesktopCreditNoteResult> ReconcileAsync(Guid caller, string reference, Guid id, CancellationToken ct)
    {
        var sale = await ReadSale(caller, reference, ct);
        var note = await db.DesktopCreditNotes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id && n.SaleId == sale.Id, ct)
            ?? throw new InvalidOperationException("Credit note not found for this sale.");
        if (note.Status is "Fiscalised" or "Rejected") return Map(note);
        var plan = JsonSerializer.Deserialize<DesktopCreditPlan>(note.PlanJson, Json)!;
        var result = await fiscal.FindAsync(plan, ct);
        if (result?.Success == true && !result.Skipped)
            return await SaveOutcome(note.Id, "Fiscalised", result, "The existing fiscal receipt was found. No new receipt was submitted.");
        return Map(note) with { Message = "No receipt was confirmed. The credit remains reserved for reconciliation; nothing was resubmitted." };
    }

    public async Task<DesktopCreditNoteResult> ContinueAsync(Guid caller, string reference, Guid id, CancellationToken ct)
    {
        var sale = await ReadSale(caller, reference, ct);
        var note = await db.DesktopCreditNotes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id && n.SaleId == sale.Id, ct)
            ?? throw new InvalidOperationException("Credit note not found for this sale.");
        return note.Status == "Prepared" ? await IssueAsync(note, ct) : Map(note);
    }

    private async Task<DesktopCreditNoteResult> SaveOutcome(Guid id, string status, FiscalizationResult? result, string? message)
    {
        var json = result is null ? null : JsonSerializer.Serialize(result, Json);
        DateTime? fiscalisedAt = status == "Fiscalised" ? DateTime.UtcNow : null;
        await db.DesktopCreditNotes.Where(n => n.Id == id && n.Status != "Fiscalised")
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, status).SetProperty(n => n.Message, message)
                .SetProperty(n => n.FiscalResultJson, json).SetProperty(n => n.FiscalisedAtUtc, fiscalisedAt), CancellationToken.None);
        try { await audit.LogAsync("DesktopCreditNote", "DesktopCreditNote", id.ToString(), message, status == "Fiscalised"); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not audit desktop credit {Id}", id); }
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
        .Where(n => n.Status != "Rejected")
        .SelectMany(n => JsonSerializer.Deserialize<DesktopCreditPlan>(n.PlanJson, Json)!.Quantities)
        .GroupBy(l => l.LineNo).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

    private static DesktopCreditNoteResult Map(DesktopCreditNoteEntity note)
    {
        var result = note.FiscalResultJson is null ? null : JsonSerializer.Deserialize<FiscalizationResult>(note.FiscalResultJson, Json);
        return new(note.Id, note.Number, note.Status, note.Amount, note.Currency, note.Reason,
            note.OriginalFiscalNumber, note.CreatedAtUtc, note.Message, result?.QRCode, result?.ReceiptGlobalNo, note.SapDocNum);
    }
}
