using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopCreditNotes;

/// <summary>What the list of every desktop credit is asked for. Dates are CAT calendar days.</summary>
public sealed record DesktopCreditNoteListQuery(
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    string? WarehouseCode = null,
    string? SourceSystem = null,
    string? Status = null,
    string? SapStatus = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>One credit, with enough of its sale to say where it came from.</summary>
public sealed record DesktopCreditNoteListRow(
    Guid Id,
    string Number,
    string Status,
    string SapStatus,
    decimal Amount,
    string Currency,
    string Reason,
    DateTime CreatedAtUtc,
    DateTime? FiscalisedAtUtc,
    string? ReceiptGlobalNo,
    string? Message,
    int? SapDocNum,
    int SapAttempts,
    string? SapError,
    string SaleReference,
    string SaleNumber,
    bool SaleIsConsolidated,
    string? SaleSourceSystem,
    string SaleWarehouseCode,
    string? SaleCardCode,
    string? SaleCardName,
    string? SaleCustomerName,
    string? SaleFiscalReceiptNumber,
    int? SaleSapDocNum);

/// <summary>
/// A page of credits, and counts over every credit the filters match regardless of the SAP-status
/// filter — so the figures still say how many have failed while the table shows only the posted.
/// </summary>
public sealed record DesktopCreditNoteListResponse(
    List<DesktopCreditNoteListRow> Items,
    int TotalCount,
    int Page,
    int PageSize,
    Dictionary<string, int> SapStatusCounts,
    Dictionary<string, int> StatusCounts,
    Dictionary<string, decimal> TotalsByCurrency,
    List<string> Warehouses);

/// <summary>
/// Every credit raised against a desktop sale — till, vending and van — in one list, and the one
/// action a list is for: sending a refused SAP memo again.
/// </summary>
/// <remarks>
/// Before this, a credit could only be found by opening its sale. <c>/credit-notes</c> reads SAP, so a
/// credit ZIMRA holds and SAP does not — the one somebody has to act on — appeared nowhere a person
/// would look. INV1753's (19 September 2026) was exactly that.
/// </remarks>
public sealed class DesktopCreditNoteListService(
    ApplicationDbContext db,
    DesktopCreditSapPoster sapPoster,
    IAuditService audit,
    ILogger<DesktopCreditNoteListService> logger)
{
    private const int MaxPageSize = 200;

    /// <summary>CAT is UTC+2 all year; a calendar day there starts at 22:00 UTC the day before.</summary>
    private static readonly TimeSpan CatOffset = TimeSpan.FromHours(2);

    public async Task<DesktopCreditNoteListResponse> ListAsync(
        Guid callerId, DesktopCreditNoteListQuery query, CancellationToken ct)
    {
        var (ownScope, scope) = await ScopeAsync(callerId, query.WarehouseCode, ct);

        var notes = db.DesktopCreditNotes.AsNoTracking();

        if (scope.WarehouseCode is { } warehouse)
        {
            notes = notes.Where(n => n.Sale.WarehouseCode == warehouse);
        }

        if (query.FromDate is { } from)
        {
            var fromUtc = DateTime.SpecifyKind(from.Date - CatOffset, DateTimeKind.Utc);
            notes = notes.Where(n => n.CreatedAtUtc >= fromUtc);
        }

        if (query.ToDate is { } to)
        {
            var toUtc = DateTime.SpecifyKind(to.Date.AddDays(1) - CatOffset, DateTimeKind.Utc);
            notes = notes.Where(n => n.CreatedAtUtc < toUtc);
        }

        if (!string.IsNullOrWhiteSpace(query.SourceSystem))
        {
            notes = query.SourceSystem.Trim() switch
            {
                // Two source systems are one van sale to anybody reading this list.
                SaleSourceSystems.VanSales or SaleSourceSystems.VanSalesOnline => notes.Where(n =>
                    n.Sale.SourceSystem == SaleSourceSystems.VanSales
                    || n.Sale.SourceSystem == SaleSourceSystems.VanSalesOnline),
                var source => notes.Where(n => n.Sale.SourceSystem == source)
            };
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim();
            notes = notes.Where(n => n.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            var docNum = int.TryParse(term, out var parsed) ? parsed : (int?)null;
            // INV1753 — the number on the customer's receipt — names the sale by its id.
            var saleId = DesktopSaleNumber.TryParse(term, out var id) ? id : (int?)null;

            notes = notes.Where(n =>
                n.Number.ToLower().Contains(term)
                || n.OriginalFiscalNumber.ToLower().Contains(term)
                || n.Sale.ExternalReferenceId.ToLower().Contains(term)
                || (n.Sale.CardCode != null && n.Sale.CardCode.ToLower().Contains(term))
                || (n.Sale.CardName != null && n.Sale.CardName.ToLower().Contains(term))
                || (n.Sale.RouteCustomerName != null && n.Sale.RouteCustomerName.ToLower().Contains(term))
                || (n.Sale.FiscalReceiptNumber != null && n.Sale.FiscalReceiptNumber == term)
                || (docNum != null && (n.SapDocNum == docNum || n.Sale.SapDocNum == docNum))
                || (saleId != null && n.SaleId == saleId));
        }

        // Counted before the SAP-status filter, on purpose — see the response's remarks.
        var sapCounts = await notes
            .Where(n => n.Status == DesktopCreditStatuses.Fiscalised)
            .GroupBy(n => n.SapStatus)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        if (!string.IsNullOrWhiteSpace(query.SapStatus))
        {
            var sapStatus = query.SapStatus.Trim();
            // A SAP status means nothing on a credit ZIMRA has not accepted: every such row still reads
            // the Deferred default, and would otherwise pad "waiting for the sale" with credits that
            // will never reach SAP at all.
            notes = notes.Where(n => n.Status == DesktopCreditStatuses.Fiscalised && n.SapStatus == sapStatus);
        }

        var statusCounts = await notes
            .GroupBy(n => n.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var total = statusCounts.Values.Sum();

        // Per currency, because a USD and a ZiG credit added together is no figure at all. Summed
        // client-side: SQLite, which the tests run on, cannot SUM a decimal column.
        var amounts = await notes
            .Where(n => n.Status == DesktopCreditStatuses.Fiscalised)
            .Select(n => new { n.Currency, n.Amount })
            .ToListAsync(ct);

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var page = Math.Max(1, query.Page);

        var rows = await notes
            .OrderByDescending(n => n.CreatedAtUtc)
            .ThenBy(n => n.Number)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new
            {
                Note = n,
                n.Sale.ExternalReferenceId,
                Consolidated = n.Sale.ConsolidationId != null,
                n.Sale.SourceSystem,
                n.Sale.WarehouseCode,
                n.Sale.CardCode,
                n.Sale.CardName,
                n.Sale.RouteCustomerName,
                n.Sale.FiscalReceiptNumber,
                n.Sale.SapDocNum
            })
            .ToListAsync(ct);

        // From the caller's own scope, not the filtered one, so choosing a warehouse does not shrink
        // the list it was chosen from to itself.
        var warehouses = ownScope.WarehouseCode is { } own
            ? [own]
            : await db.DesktopCreditNotes.AsNoTracking()
                .Select(n => n.Sale.WarehouseCode)
                .Distinct()
                .OrderBy(code => code)
                .ToListAsync(ct);

        return new DesktopCreditNoteListResponse(
            rows.Select(r => new DesktopCreditNoteListRow(
                r.Note.Id,
                r.Note.Number,
                r.Note.Status,
                r.Note.SapStatus,
                r.Note.Amount,
                r.Note.Currency,
                r.Note.Reason,
                r.Note.CreatedAtUtc,
                r.Note.FiscalisedAtUtc,
                ReceiptGlobalNo(r.Note.FiscalResultJson),
                r.Note.Message,
                r.Note.SapDocNum,
                r.Note.SapAttempts,
                r.Note.SapError,
                r.ExternalReferenceId,
                DesktopSaleNumber.Format(r.Note.SaleId),
                r.Consolidated,
                r.SourceSystem,
                r.WarehouseCode,
                r.CardCode,
                r.CardName,
                r.RouteCustomerName,
                r.FiscalReceiptNumber,
                r.SapDocNum)).ToList(),
            total,
            page,
            pageSize,
            sapCounts,
            statusCounts,
            amounts
                .GroupBy(a => string.IsNullOrWhiteSpace(a.Currency) ? "USD" : a.Currency.Trim().ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount)),
            warehouses);
    }

    /// <summary>
    /// Sends a refused SAP credit memo again, now, and answers with where it stands afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sweep retries a refused memo by itself, but only six times a minute apart and only for three
    /// days. A refusal that needs a fix deployed — INV1753's was a code bug — outlives both, and the
    /// credit then sits as Failed with nothing left that will ever look at it again. This is the way
    /// back that used to be a hand-written UPDATE.
    /// </para>
    /// <para>
    /// The attempt count goes back to zero so the sweep resumes too if this pass is refused again.
    /// Never a second memo: the poster asks SAP for one under the credit's own number before it sends,
    /// and holds off inside the grace window after a post whose reply was lost.
    /// </para>
    /// </remarks>
    public async Task<DesktopCreditNoteListRow> RetrySapAsync(Guid callerId, Guid id, CancellationToken ct)
    {
        var note = await db.DesktopCreditNotes.AsNoTracking()
            .Include(n => n.Sale)
            .SingleOrDefaultAsync(n => n.Id == id, ct)
            ?? throw new InvalidOperationException("Credit note not found.");

        await ScopeAsync(callerId, note.Sale.WarehouseCode, ct);

        if (note.Status != DesktopCreditStatuses.Fiscalised)
        {
            throw new InvalidOperationException(
                "Only a credit ZIMRA has accepted can go to SAP. Settle its fiscal outcome first.");
        }

        if (note.SapStatus != DesktopCreditSapStatuses.Failed)
        {
            throw new InvalidOperationException(note.SapStatus switch
            {
                DesktopCreditSapStatuses.Posted => $"Already in SAP as credit memo {note.SapDocNum}.",
                DesktopCreditSapStatuses.Deferred =>
                    "Nothing to retry: this credit is waiting for its sale to reach SAP and follows it automatically.",
                DesktopCreditSapStatuses.ManualInSap =>
                    "This credit cannot be posted automatically. Raise it by hand in SAP.",
                _ => "This credit owes SAP nothing."
            });
        }

        var previousAttempts = note.SapAttempts;

        await db.DesktopCreditNotes
            .Where(n => n.Id == id && n.SapStatus == DesktopCreditSapStatuses.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.SapAttempts, 0), CancellationToken.None);

        try
        {
            await audit.LogAsync(
                AuditActions.PostDesktopCreditNoteToSAP,
                nameof(DesktopCreditNoteEntity),
                note.Number,
                $"Retry of SAP credit memo for {note.Number} requested by hand after {previousAttempts} attempts",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the SAP retry of credit {CreditNote}.", note.Number);
        }

        // The poster re-reads the row itself (see its SettleAsync), so the ExecuteUpdate above is seen.
        await sapPoster.SettleAsync(id, CancellationToken.None);

        return await RowAsync(id, ct);
    }

    private async Task<DesktopCreditNoteListRow> RowAsync(Guid id, CancellationToken ct)
    {
        var n = await db.DesktopCreditNotes.AsNoTracking()
            .Include(x => x.Sale)
            .SingleAsync(x => x.Id == id, ct);

        return new DesktopCreditNoteListRow(
            n.Id, n.Number, n.Status, n.SapStatus, n.Amount, n.Currency, n.Reason, n.CreatedAtUtc,
            n.FiscalisedAtUtc, ReceiptGlobalNo(n.FiscalResultJson), n.Message, n.SapDocNum, n.SapAttempts,
            n.SapError, n.Sale.ExternalReferenceId, DesktopSaleNumber.Format(n.SaleId),
            n.Sale.ConsolidationId != null, n.Sale.SourceSystem, n.Sale.WarehouseCode,
            n.Sale.CardCode, n.Sale.CardName, n.Sale.RouteCustomerName, n.Sale.FiscalReceiptNumber,
            n.Sale.SapDocNum);
    }

    /// <summary>
    /// The caller's read scope, narrowed to the warehouse asked for. Same rule as a single sale's
    /// credits: a shop-bound account sees its own warehouse and nothing else.
    /// </summary>
    private async Task<(DesktopSalesReadScope Own, DesktopSalesReadScope Narrowed)> ScopeAsync(Guid callerId, string? warehouse, CancellationToken ct)
    {
        var caller = await db.Users.AsNoTracking().Include(u => u.Shop)
            .SingleOrDefaultAsync(u => u.Id == callerId, ct);

        var scope = DesktopSalesReadScopeResolver.Resolve(caller);
        if (scope.IsError)
        {
            throw new UnauthorizedAccessException("This account cannot access desktop sales.");
        }

        var narrowed = scope.Value.Narrow(warehouse);
        if (narrowed.IsError)
        {
            throw new UnauthorizedAccessException("That warehouse belongs to another shop.");
        }

        return (scope.Value, narrowed.Value);
    }

    private static string? ReceiptGlobalNo(string? fiscalResultJson)
    {
        if (fiscalResultJson is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FiscalizationResult>(fiscalResultJson, DesktopCreditNoteService.Json)
                ?.ReceiptGlobalNo;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
