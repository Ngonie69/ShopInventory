using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationWorkQueue;

/// <summary>
/// Builds the console's work queue from the two places an unfiscalised document can be.
/// </summary>
/// <remarks>
/// Two sources, because a document owes ZIMRA a receipt for two different reasons. A sale captured
/// outside SAP owes one because it has not been submitted yet or its submission failed; a SAP document
/// owes one because the platform was asked and said it holds no receipt for that number. They are
/// different tables, different keys and different remedies, so they are queried separately, filtered
/// separately in the database, and merged only to be shown.
///
/// Merging costs a little: to page across both, each side is read as far as the requested page reaches
/// and the two are interleaved here. That is bounded by the page size and it keeps the paging honest,
/// which the alternative — page one source and append the other — would not.
/// </remarks>
public sealed class GetFiscalisationWorkQueueHandler(
    ApplicationDbContext db,
    IOptions<DesktopSalePostingSettings> sweepSettings)
    : IRequestHandler<GetFiscalisationWorkQueueQuery, ErrorOr<FiscalWorkQueueResult>>
{
    private const string NotFiscalisedStatus = "Not Fiscalised";

    public async Task<ErrorOr<FiscalWorkQueueResult>> Handle(
        GetFiscalisationWorkQueueQuery query,
        CancellationToken cancellationToken)
    {
        // Clamped, not just floored. `reach` below is a Take on both sources, so an unbounded page
        // number is a negative Take at one end and two very large reads at the other.
        var (page, pageSize) = FiscalConsolePaging.Clamp(query.Page, query.PageSize);
        var filter = string.IsNullOrWhiteSpace(query.Status) ? FiscalWorkQueueFilters.All : query.Status.Trim();
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var reach = page * pageSize;

        var sales = FilterSales(query, filter, search);
        var documents = FilterDocuments(query, filter, search);

        var saleCount = await sales.CountAsync(cancellationToken);
        var documentCount = await documents.CountAsync(cancellationToken);

        var saleRows = await sales
            .OrderByDescending(sale => sale.CreatedAt)
            .Take(reach)
            .Select(sale => new SaleRow(
                sale.Id,
                sale.ExternalReferenceId,
                sale.SourceSystem,
                sale.CardName,
                sale.RouteCustomerName,
                sale.WarehouseCode,
                sale.TotalAmount,
                sale.Currency,
                sale.CreatedAt,
                sale.DocDate,
                sale.FiscalizationStatus,
                sale.FiscalizationRequiresReconciliation,
                sale.FiscalizationAttempts,
                sale.FiscalError,
                sale.ReceiptIngestStatus,
                sale.ReceiptIngestAttempts,
                sale.ReceiptIngestError,
                sale.FiscalDeviceId,
                sale.ReceiptGlobalNo,
                sale.SapDocNum))
            .ToListAsync(cancellationToken);

        var documentRows = await documents
            .OrderByDescending(transaction => transaction.TimestampUtc)
            .Take(reach)
            .Select(transaction => new DocumentRow(
                transaction.DocumentType,
                transaction.DocNum,
                transaction.CardName,
                transaction.DocTotal,
                transaction.Currency,
                transaction.TimestampUtc,
                transaction.Status,
                transaction.Message))
            .ToListAsync(cancellationToken);

        var sweep = SweepReach.From(sweepSettings.Value);

        var items = saleRows.Select(sale => MapSale(sale, sweep))
            .Concat(documentRows.Select(MapDocument))
            .OrderByDescending(item => item.OccurredAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var total = saleCount + documentCount;

        return new FiscalWorkQueueResult(
            items,
            total,
            saleCount,
            documentCount,
            page,
            pageSize,
            page * pageSize < total);
    }

    /// <summary>
    /// Sales that owe ZIMRA something, narrowed in the database.
    /// </summary>
    /// <remarks>
    /// <see cref="DesktopSaleFiscalizationStatus.Skipped"/> is deliberately absent from the outstanding
    /// set. Fiscalisation was switched off when that sale was made, so nothing is owed for it, and
    /// listing it would put a permanent floor under a queue whose whole value is reaching zero.
    /// </remarks>
    private IQueryable<DesktopSaleEntity> FilterSales(
        GetFiscalisationWorkQueueQuery query,
        string filter,
        string? search)
    {
        var sales = db.DesktopSales.AsNoTracking();

        sales = filter switch
        {
            FiscalWorkQueueFilters.AwaitingFiscalisation =>
                sales.Where(sale => sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Pending),

            FiscalWorkQueueFilters.FiscalisationFailed =>
                sales.Where(sale =>
                    sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Failed &&
                    !sale.FiscalizationRequiresReconciliation),

            FiscalWorkQueueFilters.NeedsReconciliation =>
                sales.Where(sale => sale.FiscalizationRequiresReconciliation),

            FiscalWorkQueueFilters.HandoverFailed =>
                sales.Where(sale => sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Failed),

            FiscalWorkQueueFilters.ChainBroken =>
                sales.Where(sale => sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.ChainBroken),

            FiscalWorkQueueFilters.Unsignable =>
                sales.Where(sale => sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unsignable),

            FiscalWorkQueueFilters.Unstamped =>
                sales.Where(sale => sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unstamped),

            _ => sales.Where(sale =>
                sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Pending ||
                sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Failed ||
                sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Failed ||
                sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.ChainBroken ||
                sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unsignable ||
                sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unstamped)
        };

        if (query.DeviceId is > 0)
        {
            sales = sales.Where(sale => sale.FiscalDeviceId == query.DeviceId);
        }

        // On the trading day rather than the row's creation instant: an operator asking for "yesterday"
        // means the day the sale was made, and a late upload would otherwise fall outside it.
        if (query.FromDate is not null)
        {
            sales = sales.Where(sale => sale.DocDate >= query.FromDate.Value.Date);
        }

        if (query.ToDate is not null)
        {
            sales = sales.Where(sale => sale.DocDate <= query.ToDate.Value.Date);
        }

        if (search is not null)
        {
            sales = sales.Where(sale =>
                EF.Functions.ILike(sale.ExternalReferenceId, $"%{search}%") ||
                (sale.CardName != null && EF.Functions.ILike(sale.CardName, $"%{search}%")) ||
                (sale.RouteCustomerName != null && EF.Functions.ILike(sale.RouteCustomerName, $"%{search}%")));
        }

        return sales;
    }

    /// <summary>
    /// SAP documents the platform holds no receipt for, one row per document.
    /// </summary>
    /// <remarks>
    /// A document accumulates a row per lookup, so the newest is the only one that describes it now —
    /// hence the "no later row exists" test rather than a group-by, which keeps the whole thing one
    /// translatable query instead of a fetch and a fold.
    ///
    /// A device filter excludes documents entirely. The platform picks the device for a SAP submission,
    /// including failing over to another one mid-flight, and nothing records which it chose against the
    /// document — so a document cannot honestly be said to belong to a device.
    ///
    /// "Owes a receipt" is <see cref="FiscalDocumentStatusProjector.LacksFiscalEvidenceExpression"/>,
    /// not <c>Status != "Fiscalised"</c>. Those are not the same set, and the difference is this
    /// console's own successful retries: the manual fiscalise path writes <c>Status = "Success"</c>,
    /// so testing the one status left every document this page fiscalised sitting in the queue, still
    /// offering a Fiscalise button, and the count could never reach zero.
    /// </remarks>
    private IQueryable<DesktopFiscalTransactionEntity> FilterDocuments(
        GetFiscalisationWorkQueueQuery query,
        string filter,
        string? search)
    {
        var appliesToDocuments = filter
            is FiscalWorkQueueFilters.All
            or FiscalWorkQueueFilters.AwaitingFiscalisation
            or FiscalWorkQueueFilters.FiscalisationFailed;

        if (!appliesToDocuments || query.DeviceId is > 0)
        {
            return db.DesktopFiscalTransactions.AsNoTracking().Where(_ => false);
        }

        var documents = db.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction => !db.DesktopFiscalTransactions.Any(later =>
                later.DocumentType == transaction.DocumentType &&
                later.DocNum == transaction.DocNum &&
                (later.LastSyncedAtUtc > transaction.LastSyncedAtUtc ||
                    (later.LastSyncedAtUtc == transaction.LastSyncedAtUtc && later.Id > transaction.Id))))
            .Where(transaction => transaction.DocNum > 0)
            .Where(FiscalDocumentStatusProjector.LacksFiscalEvidenceExpression);

        documents = filter switch
        {
            FiscalWorkQueueFilters.AwaitingFiscalisation =>
                documents.Where(transaction => transaction.Status == NotFiscalisedStatus),

            FiscalWorkQueueFilters.FiscalisationFailed =>
                documents.Where(transaction => transaction.Status != NotFiscalisedStatus),

            _ => documents
        };

        if (query.FromDate is not null)
        {
            documents = documents.Where(transaction => transaction.TimestampUtc >= query.FromDate.Value.Date);
        }

        if (query.ToDate is not null)
        {
            var exclusiveEnd = query.ToDate.Value.Date.AddDays(1);
            documents = documents.Where(transaction => transaction.TimestampUtc < exclusiveEnd);
        }

        if (search is not null)
        {
            var docNum = int.TryParse(search, out var parsed) ? parsed : (int?)null;

            documents = documents.Where(transaction =>
                (transaction.CardName != null && EF.Functions.ILike(transaction.CardName, $"%{search}%")) ||
                (docNum != null && transaction.DocNum == docNum));
        }

        return documents;
    }

    /// <summary>
    /// Turns a sale into a queue entry, reporting the worst of what is wrong with it.
    /// </summary>
    /// <remarks>
    /// A sale can be behind on both steps at once — unfiscalised and holding a receipt the platform
    /// refused — and showing it twice would double the queue while halving its meaning. The order below
    /// is by how much it costs to be wrong about: a broken chain stops a whole device, an unsignable
    /// receipt is money taken against a receipt ZIMRA will never see, and an unfiscalised sale is one
    /// document.
    ///
    /// Ordering is load-bearing between the last few arms, not merely tidy. The van upload writes an
    /// unstamped sale as <see cref="DesktopSaleFiscalizationStatus.Failed"/> *and*
    /// <see cref="DesktopSaleReceiptIngestStatus.Unstamped"/> — both, on the same row — so with the
    /// failure arm first the unstamped arm was unreachable for every real row, and a sale that was never
    /// stamped at all read as "fiscalisation failed, the background sweep retries this". Both halves of
    /// that were false: nothing had been stamped, and the drain skips unstamped rows on purpose.
    ///
    /// Severity comes from <see cref="FiscalWorkQueueFilters.SeverityOf"/> rather than a literal per arm,
    /// so a row's dot is the same colour as the swatch on the filter that selects it.
    /// </remarks>
    private static FiscalConsoleWorkItemDto MapSale(SaleRow sale, SweepReach sweep)
    {
        var (stage, status, severity, disposition, note, error, attempts) = sale switch
        {
            { ReceiptIngestStatus: DesktopSaleReceiptIngestStatus.ChainBroken } => (
                "Hand-over",
                "Chain broken",
                FiscalWorkQueueFilters.SeverityOf(FiscalWorkQueueFilters.ChainBroken),
                FiscalWorkQueueDispositions.Unrecoverable,
                "This receipt does not continue its device's chain. Resending cannot repair it, and every "
                    + "later receipt from the same handset is held behind it until someone reconciles the device.",
                sale.ReceiptIngestError,
                sale.ReceiptIngestAttempts),

            { ReceiptIngestStatus: DesktopSaleReceiptIngestStatus.Unsignable } => (
                "Hand-over",
                "Unsignable",
                FiscalWorkQueueFilters.SeverityOf(FiscalWorkQueueFilters.Unsignable),
                FiscalWorkQueueDispositions.Unrecoverable,
                "The upload carried no usable signature, so the platform can never archive this receipt. "
                    + "The sale itself stands — the money is real — but the fiscal side needs a person.",
                sale.ReceiptIngestError,
                sale.ReceiptIngestAttempts),

            { FiscalizationRequiresReconciliation: true } => (
                "Fiscalisation",
                "Unresolved",
                FiscalWorkQueueFilters.SeverityOf(FiscalWorkQueueFilters.NeedsReconciliation),
                FiscalWorkQueueDispositions.Reconcile,
                "The platform could not say whether the receipt was signed. Look it up on the fiscalisation "
                    + "console before anything else — a second submission cannot be withdrawn.",
                sale.FiscalError,
                sale.FiscalizationAttempts),

            // Ahead of the failure arm: the upload writes both statuses on an unstamped sale, and this is
            // the one that describes it.
            { ReceiptIngestStatus: DesktopSaleReceiptIngestStatus.Unstamped } => (
                "Fiscalisation",
                "Never stamped",
                FiscalWorkQueueFilters.SeverityOf(FiscalWorkQueueFilters.Unstamped),
                FiscalWorkQueueDispositions.Unrecoverable,
                "The handset stamped nothing for this sale: it is on a build older than the signing "
                    + "release, so it took no number off the device's chain. There is nothing to send — the "
                    + "hand-over drain skips these deliberately, because a sale that holds no place in the "
                    + "chain must not stop a device that is otherwise fine. "
                    + (sale.SourceSystem == SaleSourceSystems.VanSalesOnline
                        // The online path still fiscalises an unstamped sale from the invoice, so this one
                        // is declared — just not by the handset that made it, and not on its chain. Saying
                        // "ZIMRA has no record" here would send someone looking for a missing receipt that
                        // is not missing, and hide the thing that is actually wrong.
                        ? "The server fiscalised the invoice instead, on a device that is not this van's, "
                          + "so the receipt exists but sits on the wrong chain — which is the problem, not "
                          + "the absence of one. "
                        : "The money is real, nothing was printed for the customer, and ZIMRA has no "
                          + "record of it. ")
                    + "Update the handset so the next sale is stamped.",
                sale.ReceiptIngestError ?? sale.FiscalError,
                sale.ReceiptIngestAttempts),

            { FiscalizationStatus: DesktopSaleFiscalizationStatus.Failed } => Fiscalisation(
                sale,
                sweep,
                "Fiscalisation failed",
                FiscalWorkQueueFilters.FiscalisationFailed,
                "The background sweep retries this. Nothing was recorded at FDMS, so the sale is safe "
                    + "where it is."),

            { ReceiptIngestStatus: DesktopSaleReceiptIngestStatus.Failed } => (
                "Hand-over",
                "Hand-over failed",
                FiscalWorkQueueFilters.SeverityOf(FiscalWorkQueueFilters.HandoverFailed),
                // The drain takes van sales only. Nothing else writes a hand-over status today, but a row
                // from anywhere else would be owned by nobody and must not claim otherwise.
                SaleSourceSystems.IsVanSale(sale.SourceSystem)
                    ? FiscalWorkQueueDispositions.Automatic
                    : FiscalWorkQueueDispositions.Stalled,
                SaleSourceSystems.IsVanSale(sale.SourceSystem)
                    ? "The receipt exists and the customer has it. The drain will offer it to the platform again."
                    : "The receipt exists and the customer has it, but the hand-over drain reads van sales "
                        + "only — nothing will offer this one to the platform again on its own.",
                sale.ReceiptIngestError,
                sale.ReceiptIngestAttempts),

            _ => Fiscalisation(
                sale,
                sweep,
                "Awaiting fiscalisation",
                FiscalWorkQueueFilters.AwaitingFiscalisation,
                "Queued. The background sweep picks this up.")
        };

        return new FiscalConsoleWorkItemDto(
            Key: $"sale:{sale.Id}",
            Source: DescribeSource(sale.SourceSystem),
            SaleId: sale.Id,
            DocNum: sale.SapDocNum,
            Reference: sale.ExternalReferenceId,
            CustomerName: sale.RouteCustomerName ?? sale.CardName,
            WarehouseCode: sale.WarehouseCode,
            Amount: sale.TotalAmount,
            Currency: sale.Currency,
            OccurredAtUtc: sale.CreatedAt,
            Stage: stage,
            Status: status,
            Severity: severity,
            DeviceId: sale.FiscalDeviceId,
            ReceiptGlobalNo: sale.ReceiptGlobalNo,
            Attempts: attempts,
            Error: error,
            Disposition: disposition,
            DispositionNote: note);
    }

    /// <summary>
    /// The fiscalisation arms, which differ only in what they are called: what may be done about them is
    /// the same question, and it is not answered by the status.
    /// </summary>
    private static (string Stage, string Status, string Severity, string Disposition, string Note,
        string? Error, int Attempts) Fiscalisation(
        SaleRow sale,
        SweepReach sweep,
        string status,
        string filter,
        string sweptNote)
    {
        var (disposition, note) = sweep.Owns(sale, out var reason)
            ? (FiscalWorkQueueDispositions.Automatic, sweptNote)
            : (FiscalWorkQueueDispositions.Stalled, reason);

        return (
            "Fiscalisation",
            status,
            FiscalWorkQueueFilters.SeverityOf(filter),
            disposition,
            note,
            sale.FiscalError,
            sale.FiscalizationAttempts);
    }

    private static FiscalConsoleWorkItemDto MapDocument(DocumentRow document)
    {
        var isInvoice = string.Equals(document.DocumentType, "Invoice", StringComparison.OrdinalIgnoreCase);
        var eligible = string.Equals(document.Status, NotFiscalisedStatus, StringComparison.OrdinalIgnoreCase);
        var unresolved = IsUnresolved(document.Message);

        var (status, severity, disposition, note) = (unresolved, eligible, isInvoice) switch
        {
            // Ahead of everything, including the credit-note case: an unresolved outcome is the one row
            // on this page where offering the wrong action cannot be taken back.
            (true, _, _) => (
                "Unresolved",
                FiscalWorkQueueFilters.SeverityOf(FiscalWorkQueueFilters.NeedsReconciliation),
                FiscalWorkQueueDispositions.Reconcile,
                "The last attempt could not establish whether a receipt was signed for this document. "
                    + "Look the number up on the fiscalisation platform before anything else — a second "
                    + "submission cannot be withdrawn."),

            (false, _, true) => (
                eligible ? "Not fiscalised" : document.Status,
                FiscalWorkQueueFilters.SeverityOf(
                    eligible ? FiscalWorkQueueFilters.AwaitingFiscalisation : FiscalWorkQueueFilters.FiscalisationFailed),
                FiscalWorkQueueDispositions.Retry,
                "The platform holds no receipt for this document number. Fiscalising it submits the "
                    + "invoice as SAP holds it."),

            // Only an invoice has a manual route. A credit note is fiscalised alongside the document
            // that carries it, and there is no endpoint that would let this page send one on its own.
            _ => (
                eligible ? "Not fiscalised" : document.Status,
                FiscalWorkQueueFilters.SeverityOf(
                    eligible ? FiscalWorkQueueFilters.AwaitingFiscalisation : FiscalWorkQueueFilters.FiscalisationFailed),
                FiscalWorkQueueDispositions.Automatic,
                "Credit notes fiscalise with their document sync; there is no single-document route "
                    + "from here.")
        };

        return new FiscalConsoleWorkItemDto(
            Key: $"doc:{document.DocumentType}:{document.DocNum}",
            Source: isInvoice ? "SAP invoice" : "SAP credit note",
            SaleId: null,
            DocNum: document.DocNum,
            Reference: $"#{document.DocNum}",
            CustomerName: document.CardName,
            WarehouseCode: null,
            Amount: document.DocTotal,
            Currency: string.IsNullOrWhiteSpace(document.Currency) ? string.Empty : document.Currency,
            OccurredAtUtc: document.TimestampUtc,
            Stage: "Fiscalisation",
            Status: status,
            Severity: severity,
            DeviceId: null,
            ReceiptGlobalNo: null,
            Attempts: 0,
            Error: document.Message,
            Disposition: disposition,
            DispositionNote: note);
    }

    /// <summary>
    /// Whether the message recorded against a SAP document says the outcome was never established.
    /// </summary>
    /// <remarks>
    /// Read out of prose, which is not where a verdict this expensive belongs, and it is worth saying why
    /// it is here. A sale records the verdict properly, in
    /// <c>DesktopSaleEntity.FiscalizationRequiresReconciliation</c>, and the queue reads that column. A
    /// SAP document has no equivalent column, and the manual fiscalise path collapses a reconciliation
    /// result to <c>Status = "Failed"</c> before writing it — so after a reload the row is
    /// indistinguishable from a plain refusal and is offered a Fiscalise button again. The console's
    /// in-session lock-out closes that window only until someone presses F5.
    ///
    /// Until the transaction row can carry the verdict itself, the wording the fiscalisation service
    /// writes alongside it is the only surviving trace. The markers are matched as a family and
    /// case-folded, and the set is deliberately generous: reading an ambiguous outcome as an ordinary
    /// failure invites the retry that signs one sale twice, while reading an ordinary failure as
    /// ambiguous costs a look-up.
    /// </remarks>
    private static bool IsUnresolved(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var folded = message.ToLowerInvariant();

        return UnresolvedMarkers.Any(marker => folded.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>Lower-cased, because <see cref="IsUnresolved"/> folds the message before matching.</summary>
    private static readonly string[] UnresolvedMarkers =
    [
        // "The fiscal outcome is unresolved. Check the receipt on the fiscalisation console before any
        // resubmission — it may already exist." — FiscalizationService, on RequiresReconciliation.
        "unresolved",
        "reconcil",
        "indeterminate",
        "idempotency_",
        "chainbreak"
    ];

    /// <summary>
    /// What to call the row's origin on screen.
    /// </summary>
    /// <remarks>
    /// The two van sources are named apart because what to do about them differs. An offline van sale is
    /// still owed to SAP and its row is the sale; an online one is already invoiced and its row exists
    /// only to carry the receipt, so "no SAP document yet" is normal for the first and would be a fault
    /// in the second. Telling a reader they are the same thing sends them looking for the wrong problem.
    /// </remarks>
    private static string DescribeSource(string? sourceSystem) => sourceSystem switch
    {
        SaleSourceSystems.VanSales => "Van sale (offline)",
        SaleSourceSystems.VanSalesOnline => "Van sale (online)",
        SaleSourceSystems.ShopTill => "Shop till",
        SaleSourceSystems.Vending => "Vending",
        _ => "Desktop sale"
    };

    /// <summary>
    /// Exactly what <c>DesktopSaleFiscalisationSweep</c> will select on its next pass.
    /// </summary>
    /// <remarks>
    /// The console used to tell every failed sale it was "handled automatically". The sweep takes vending
    /// sales only, inside a lookback window, under an attempt budget — so for a shop-till sale, a van
    /// sale, a sale older than the window or one out of attempts, that cell named an owner who does not
    /// exist and the row sat there being ignored by everybody.
    ///
    /// The three conditions are restated here from the sweep's own query rather than shared with it,
    /// because the sweep is a write path and this is a read: the two must agree, and this type exists so
    /// that when they stop agreeing it is one obvious place, not a sentence in a tooltip. The settings
    /// come from the same <see cref="DesktopSalePostingSettings"/> the sweep is configured by, so the
    /// numbers at least can never drift.
    /// </remarks>
    private sealed record SweepReach(DateTime Cutoff, int LookbackDays, int MaxAttempts)
    {
        public static SweepReach From(DesktopSalePostingSettings settings) => new(
            DateTime.UtcNow.Date.AddDays(-settings.LookbackDays),
            settings.LookbackDays,
            settings.MaxFiscalisationAttempts);

        /// <summary>Whether the sweep will pick this sale up, and if not, what to tell the operator.</summary>
        public bool Owns(SaleRow sale, out string reason)
        {
            if (!string.Equals(sale.SourceSystem, SaleSourceSystems.Vending, StringComparison.Ordinal))
            {
                reason = $"Nothing will pick this up on its own. The fiscalisation sweep reads vending "
                    + $"sales only — a {DescribeSource(sale.SourceSystem).ToLowerInvariant()} is fiscalised "
                    + "as it is made — so this row stays here until it is resolved at the source.";
                return false;
            }

            if (sale.FiscalizationAttempts >= MaxAttempts)
            {
                reason = $"The sweep has already offered this to the platform {sale.FiscalizationAttempts} "
                    + $"time(s), which is its budget of {MaxAttempts}. It will not be tried again on its own.";
                return false;
            }

            if (sale.DocDate.Date < Cutoff)
            {
                reason = $"The sweep looks back {LookbackDays} day(s) and this sale is older than that, so "
                    + "no scheduled run selects it any more.";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    private sealed record SaleRow(
        int Id,
        string ExternalReferenceId,
        string? SourceSystem,
        string? CardName,
        string? RouteCustomerName,
        string WarehouseCode,
        decimal TotalAmount,
        string Currency,
        DateTime CreatedAt,
        DateTime DocDate,
        DesktopSaleFiscalizationStatus FiscalizationStatus,
        bool FiscalizationRequiresReconciliation,
        int FiscalizationAttempts,
        string? FiscalError,
        DesktopSaleReceiptIngestStatus ReceiptIngestStatus,
        int ReceiptIngestAttempts,
        string? ReceiptIngestError,
        int? FiscalDeviceId,
        int? ReceiptGlobalNo,
        int? SapDocNum);

    private sealed record DocumentRow(
        string DocumentType,
        int DocNum,
        string? CardName,
        decimal DocTotal,
        string? Currency,
        DateTime TimestampUtc,
        string Status,
        string? Message);
}
