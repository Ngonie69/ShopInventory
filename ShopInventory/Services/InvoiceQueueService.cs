using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Service for managing the invoice posting queue
/// </summary>
public interface IInvoiceQueueService
{
    /// <summary>
    /// Enqueue an invoice for batch posting to SAP
    /// </summary>
    /// <remarks>
    /// <c>salesOrderId</c> is the local sales order the invoice was converted from, so consolidation can
    /// base the invoice on it.
    /// </remarks>
    Task<InvoiceQueueResultDto> EnqueueInvoiceAsync(
        CreateStockReservationRequest request,
        string reservationId,
        string? createdBy = null,
        CancellationToken cancellationToken = default,
        int? salesOrderId = null);

    /// <summary>
    /// Get the status of a queued invoice by external reference
    /// </summary>
    Task<InvoiceQueueStatusDto?> GetQueueStatusAsync(
        string externalReference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the status of a queued invoice by reservation ID
    /// </summary>
    Task<InvoiceQueueStatusDto?> GetQueueStatusByReservationAsync(
        string reservationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all pending/processing invoices in the queue
    /// </summary>
    Task<List<InvoiceQueueStatusDto>> GetPendingInvoicesAsync(
        string? sourceSystem = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get invoices requiring manual review
    /// </summary>
    Task<List<InvoiceQueueStatusDto>> GetInvoicesRequiringReviewAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a queued invoice (only if pending)
    /// </summary>
    Task<bool> CancelQueuedInvoiceAsync(
        string externalReference,
        string? cancelledBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retry a failed invoice
    /// </summary>
    Task<bool> RetryInvoiceAsync(
        string externalReference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update queue entry after SAP posting
    /// </summary>
    Task UpdateQueueEntryAsync(
        int queueId,
        InvoiceQueueStatus status,
        string? sapDocEntry = null,
        int? sapDocNum = null,
        string? error = null,
        string? fiscalDeviceNumber = null,
        string? fiscalReceiptNumber = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get next batch of invoices to process
    /// </summary>
    Task<List<InvoiceQueueEntity>> GetNextBatchForProcessingAsync(
        int batchSize = 5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark an invoice as processing
    /// </summary>
    Task MarkAsProcessingAsync(int queueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all fiscalized invoices ready for end-of-day consolidation
    /// </summary>
    Task<List<InvoiceQueueEntity>> GetFiscalizedInvoicesAsync(
        DateTime? date = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks fiscalized invoices completed after consolidation posts them to SAP, and takes the units
    /// their reservations were holding off the stock ledger.
    /// </summary>
    /// <remarks>
    /// The two halves belong in one call because between them the units are counted either twice or
    /// not at all. Until this runs the queued sale is on the ledger only as a reservation hold; after
    /// it, only as a decrement. See <see cref="ShopInventory.Common.Stock.ReservationHolds"/>.
    /// </remarks>
    Task MarkAsConsolidatedAsync(
        IEnumerable<int> queueIds,
        string sapDocEntry,
        int sapDocNum,
        CancellationToken cancellationToken = default);
}

public class InvoiceQueueService : IInvoiceQueueService
{
    private readonly ApplicationDbContext _context;
    private readonly IStockLedger _stockLedger;
    private readonly ILogger<InvoiceQueueService> _logger;

    public InvoiceQueueService(
        ApplicationDbContext context,
        IStockLedger stockLedger,
        ILogger<InvoiceQueueService> logger)
    {
        _context = context;
        _stockLedger = stockLedger;
        _logger = logger;
    }

    public async Task<InvoiceQueueResultDto> EnqueueInvoiceAsync(
        CreateStockReservationRequest request,
        string reservationId,
        string? createdBy = null,
        CancellationToken cancellationToken = default,
        int? salesOrderId = null)
    {
        try
        {
            // Check if already queued
            var existing = await _context.InvoiceQueue
                .FirstOrDefaultAsync(q => q.ExternalReference == request.ExternalReference, cancellationToken);

            if (existing != null)
            {
                return new InvoiceQueueResultDto
                {
                    Success = false,
                    ErrorCode = "ALREADY_QUEUED",
                    ErrorMessage = $"Invoice with reference '{request.GetExternalReference()}' is already queued",
                    ReservationId = existing.ReservationId,
                    QueueId = existing.Id,
                    Status = existing.Status.ToString()
                };
            }

            // Calculate total amount
            decimal totalAmount = request.Lines.Sum(l => l.Quantity * l.UnitPrice);

            var queueEntry = new InvoiceQueueEntity
            {
                ReservationId = reservationId,
                ExternalReference = request.GetExternalReference(),
                CustomerCode = request.CardCode,
                InvoicePayload = JsonSerializer.Serialize(request, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }),
                Status = InvoiceQueueStatus.Pending,
                SourceSystem = request.SourceSystem ?? "Desktop",
                WarehouseCode = request.Lines.FirstOrDefault()?.WarehouseCode,
                TotalAmount = totalAmount,
                Currency = "USD",
                RequiresFiscalization = request.RequiresFiscalization,
                Priority = request.Priority ?? 0,
                CreatedBy = createdBy,
                Notes = request.Notes,
                SalesOrderId = salesOrderId,
                CreatedAt = DateTime.UtcNow,
                MaxRetries = 3
            };

            _context.InvoiceQueue.Add(queueEntry);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Invoice queued successfully: ExternalRef={ExternalReference}, ReservationId={ReservationId}, QueueId={QueueId}",
                request.ExternalReference, reservationId, queueEntry.Id);

            return new InvoiceQueueResultDto
            {
                Success = true,
                ReservationId = reservationId,
                QueueId = queueEntry.Id,
                ExternalReference = request.ExternalReference,
                Status = InvoiceQueueStatus.Pending.ToString(),
                EstimatedProcessingTime = TimeSpan.FromSeconds(30) // Rough estimate
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue invoice: {ExternalReference}", request.ExternalReference);
            return new InvoiceQueueResultDto
            {
                Success = false,
                ErrorCode = "QUEUE_ERROR",
                ErrorMessage = ex.Message,
                ReservationId = reservationId
            };
        }
    }

    public async Task<InvoiceQueueStatusDto?> GetQueueStatusAsync(
        string externalReference,
        CancellationToken cancellationToken = default)
    {
        var entry = await _context.InvoiceQueue
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.ExternalReference == externalReference, cancellationToken);

        return entry == null ? null : MapToStatusDto(entry);
    }

    public async Task<InvoiceQueueStatusDto?> GetQueueStatusByReservationAsync(
        string reservationId,
        CancellationToken cancellationToken = default)
    {
        var entry = await _context.InvoiceQueue
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.ReservationId == reservationId, cancellationToken);

        return entry == null ? null : MapToStatusDto(entry);
    }

    public async Task<List<InvoiceQueueStatusDto>> GetPendingInvoicesAsync(
        string? sourceSystem = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var query = _context.InvoiceQueue
            .AsNoTracking()
            .Where(q => q.Status == InvoiceQueueStatus.Pending ||
                        q.Status == InvoiceQueueStatus.Processing ||
                        q.Status == InvoiceQueueStatus.Failed);

        if (!string.IsNullOrEmpty(sourceSystem))
        {
            query = query.Where(q => q.SourceSystem == sourceSystem);
        }

        var entries = await query
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return entries.Select(MapToStatusDto).ToList();
    }

    public async Task<List<InvoiceQueueStatusDto>> GetInvoicesRequiringReviewAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var entries = await _context.InvoiceQueue
            .AsNoTracking()
            .Where(q => q.Status == InvoiceQueueStatus.RequiresReview)
            .OrderBy(q => q.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return entries.Select(MapToStatusDto).ToList();
    }

    public async Task<bool> CancelQueuedInvoiceAsync(
        string externalReference,
        string? cancelledBy = null,
        CancellationToken cancellationToken = default)
    {
        var entry = await _context.InvoiceQueue
            .AsTracking()
            .FirstOrDefaultAsync(q => q.ExternalReference == externalReference &&
                                      q.Status == InvoiceQueueStatus.Pending,
                cancellationToken);

        if (entry == null)
        {
            _logger.LogWarning("Cannot cancel invoice {ExternalReference}: not found or not pending", externalReference);
            return false;
        }

        entry.Status = InvoiceQueueStatus.Cancelled;
        entry.ProcessedAt = DateTime.UtcNow;
        entry.Notes = $"{entry.Notes} | Cancelled by {cancelledBy ?? "system"} at {DateTime.UtcNow:O}";

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Invoice cancelled: {ExternalReference}", externalReference);
        return true;
    }

    public async Task<bool> RetryInvoiceAsync(
        string externalReference,
        CancellationToken cancellationToken = default)
    {
        var entry = await _context.InvoiceQueue
            .AsTracking()
            .FirstOrDefaultAsync(q => q.ExternalReference == externalReference &&
                                      (q.Status == InvoiceQueueStatus.Failed ||
                                       q.Status == InvoiceQueueStatus.RequiresReview),
                cancellationToken);

        if (entry == null)
        {
            return false;
        }

        entry.Status = InvoiceQueueStatus.Pending;
        entry.RetryCount = 0;
        entry.NextRetryAt = null;
        entry.LastError = null;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Invoice reset for retry: {ExternalReference}", externalReference);
        return true;
    }

    public async Task<List<InvoiceQueueEntity>> GetNextBatchForProcessingAsync(
        int batchSize = 5,
        CancellationToken cancellationToken = default)
    {
        // Get pending invoices and failed ones that are ready for retry
        var now = DateTime.UtcNow;

        var entries = await _context.InvoiceQueue
            .Where(q => q.Status == InvoiceQueueStatus.Pending ||
                       (q.Status == InvoiceQueueStatus.Failed &&
                        q.RetryCount < q.MaxRetries &&
                        (q.NextRetryAt == null || q.NextRetryAt <= now)))
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        return entries;
    }

    public async Task MarkAsProcessingAsync(int queueId, CancellationToken cancellationToken = default)
    {
        var entry = await _context.InvoiceQueue.FindAsync(new object[] { queueId }, cancellationToken);
        if (entry != null)
        {
            entry.Status = InvoiceQueueStatus.Processing;
            entry.ProcessingStartedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<List<InvoiceQueueEntity>> GetFiscalizedInvoicesAsync(
        DateTime? date = null,
        CancellationToken cancellationToken = default)
    {
        // Consolidation is for the desktop route, where a day's till sales become one SAP invoice.
        //
        // A van sale must never be swept into one. It posts one-to-one so that each SAP invoice maps to
        // exactly one ZIMRA receipt, and consolidating it replaces its own reference in
        // U_Van_saleorder with the group's CONSOL-{date}-{cardCode} key. That reference is the only
        // thing that later finds the sale in SAP, so once it is gone the duplicate check cannot see the
        // invoice and a re-run posts the sale a second time.
        //
        // The filter is on the source rather than on a flag because it is a property of where the sale
        // came from, not of how this entry happens to be marked.
        var query = _context.InvoiceQueue
            .Where(q => q.Status == InvoiceQueueStatus.Fiscalized
                        && q.SourceSystem != SaleSourceSystems.VanSales);

        if (date.HasValue)
        {
            var startOfDay = date.Value.Date;
            var endOfDay = startOfDay.AddDays(1);
            query = query.Where(q => q.CreatedAt >= startOfDay && q.CreatedAt < endOfDay);
        }

        return await query
            .OrderBy(q => q.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task MarkAsConsolidatedAsync(
        IEnumerable<int> queueIds,
        string sapDocEntry,
        int sapDocNum,
        CancellationToken cancellationToken = default)
    {
        // Entries already Completed are passed over rather than re-marked. Their units came off the
        // ledger the first time round, and taking them again would lose them for the rest of the day.
        var entries = await _context.InvoiceQueue
            .Where(q => queueIds.Contains(q.Id) && q.Status != InvoiceQueueStatus.Completed)
            .ToListAsync(cancellationToken);

        if (entries.Count == 0)
        {
            return;
        }

        foreach (var entry in entries)
        {
            entry.Status = InvoiceQueueStatus.Completed;
            entry.SapDocEntry = sapDocEntry;
            entry.SapDocNum = sapDocNum;
            entry.ProcessedAt = DateTime.UtcNow;
        }

        await RecordConsolidatedUnitsLeavingAsync(entries, sapDocNum, cancellationToken);
    }

    /// <summary>
    /// Takes the units these entries' reservations were holding off the stock ledger, and saves that
    /// together with the statuses set above.
    /// </summary>
    /// <remarks>
    /// <para><b>Why here and not in the handler.</b> This is the one place that says a queued sale has
    /// reached SAP, and it is the moment the hold has to become a decrement. Until it runs the units
    /// are accounted for by the reservation hold alone — the snapshot row is untouched, which is also
    /// why <c>StockLedgerDivergenceJob</c> cannot see the sale: it only asks SAP about rows whose
    /// quantity has moved. Marking the entries Completed ends that hold, so if nothing took the units
    /// here they would simply reappear on the ledger while SAP had just issued them.</para>
    ///
    /// <para><b>One save, both halves.</b> The queue entries above are tracked on the same scoped
    /// context the ledger writes through, so the ledger's <c>SaveChangesAsync</c> commits the status
    /// changes and the drawn-down snapshot rows together. Neither can land without the other, which is
    /// what "counted exactly once" needs; a failure here leaves the entries Fiscalized and the hold
    /// standing, and the next consolidation run retries both.</para>
    ///
    /// <para><b>Settled rather than committed.</b> The consolidated invoice is in SAP by the time this
    /// is called, so the ledger cannot refuse it. A shortfall is logged — it says the ledger and the
    /// shelf had already drifted — and is not a reason to stop.</para>
    /// </remarks>
    private async Task RecordConsolidatedUnitsLeavingAsync(
        List<InvoiceQueueEntity> entries,
        int sapDocNum,
        CancellationToken cancellationToken)
    {
        var reservationIds = entries
            .Select(entry => entry.ReservationId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

        var taken = reservationIds.Count == 0
            ? []
            : await _context.StockReservationLines
                .Where(line => reservationIds.Contains(line.Reservation.ReservationId)
                            && line.ItemCode != string.Empty
                            && line.WarehouseCode != string.Empty)
                .Select(line => new StockLedgerLine(line.ItemCode, line.WarehouseCode, line.ReservedQuantity))
                .ToListAsync(cancellationToken);

        if (taken.Count == 0)
        {
            // Nothing reserved — a queue entry whose reservation was never created, or was cleared.
            // The statuses still have to be written.
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        var shortfalls = await _stockLedger.TakeSettledAsync(
            taken,
            $"queued invoices consolidated as invoice {sapDocNum}",
            $"consolidated-invoice:{sapDocNum}",
            cancellationToken);

        foreach (var shortfall in shortfalls)
        {
            _logger.LogWarning(
                "Consolidated invoice {DocNum} took {Taken} of {ItemCode} from {WarehouseCode}, but the "
                + "stock ledger held only {Held}",
                sapDocNum, shortfall.Taken, shortfall.ItemCode, shortfall.WarehouseCode, shortfall.Held);
        }
    }

    public async Task UpdateQueueEntryAsync(
        int queueId,
        InvoiceQueueStatus status,
        string? sapDocEntry = null,
        int? sapDocNum = null,
        string? error = null,
        string? fiscalDeviceNumber = null,
        string? fiscalReceiptNumber = null,
        CancellationToken cancellationToken = default)
    {
        var entry = await _context.InvoiceQueue.FindAsync(new object[] { queueId }, cancellationToken);
        if (entry == null) return;

        entry.Status = status;

        if (status == InvoiceQueueStatus.Completed || status == InvoiceQueueStatus.RequiresReview ||
            status == InvoiceQueueStatus.Fiscalized)
        {
            entry.ProcessedAt = DateTime.UtcNow;
        }

        if (!string.IsNullOrEmpty(sapDocEntry))
        {
            entry.SapDocEntry = sapDocEntry;
        }

        if (sapDocNum.HasValue)
        {
            entry.SapDocNum = sapDocNum;
        }

        if (!string.IsNullOrEmpty(error))
        {
            entry.LastError = error.Length > 2000 ? error.Substring(0, 2000) : error;
            entry.RetryCount++;

            if (entry.RetryCount < entry.MaxRetries)
            {
                // Exponential backoff: 30s, 60s, 120s...
                var delaySeconds = 30 * Math.Pow(2, entry.RetryCount - 1);
                entry.NextRetryAt = DateTime.UtcNow.AddSeconds(delaySeconds);
            }
        }

        if (!string.IsNullOrEmpty(fiscalDeviceNumber))
        {
            entry.FiscalDeviceNumber = fiscalDeviceNumber;
        }

        if (!string.IsNullOrEmpty(fiscalReceiptNumber))
        {
            entry.FiscalReceiptNumber = fiscalReceiptNumber;
            entry.FiscalizationSuccess = true;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static InvoiceQueueStatusDto MapToStatusDto(InvoiceQueueEntity entry)
    {
        var now = DateTime.UtcNow;
        var waitTime = entry.ProcessedAt.HasValue
            ? entry.ProcessedAt.Value - entry.CreatedAt
            : now - entry.CreatedAt;

        return new InvoiceQueueStatusDto
        {
            QueueId = entry.Id,
            ExternalReference = entry.ExternalReference,
            ReservationId = entry.ReservationId,
            CustomerCode = entry.CustomerCode,
            SalesOrderId = entry.SalesOrderId,
            Status = entry.Status.ToString(),
            StatusCode = (int)entry.Status,
            RetryCount = entry.RetryCount,
            MaxRetries = entry.MaxRetries,
            LastError = entry.LastError,
            SapDocEntry = entry.SapDocEntry,
            SapDocNum = entry.SapDocNum,
            FiscalDeviceNumber = entry.FiscalDeviceNumber,
            FiscalReceiptNumber = entry.FiscalReceiptNumber,
            CreatedAt = entry.CreatedAt,
            ProcessingStartedAt = entry.ProcessingStartedAt,
            ProcessedAt = entry.ProcessedAt,
            NextRetryAt = entry.NextRetryAt,
            SourceSystem = entry.SourceSystem,
            WarehouseCode = entry.WarehouseCode,
            TotalAmount = entry.TotalAmount,
            Currency = entry.Currency,
            WaitTimeSeconds = (int)waitTime.TotalSeconds,
            IsComplete = entry.Status == InvoiceQueueStatus.Completed,
            IsFailed = entry.Status == InvoiceQueueStatus.Failed ||
                       entry.Status == InvoiceQueueStatus.RequiresReview,
            CanRetry = entry.Status == InvoiceQueueStatus.Failed &&
                       entry.RetryCount < entry.MaxRetries,
            CanCancel = entry.Status == InvoiceQueueStatus.Pending
        };
    }
}

#region DTOs

public class InvoiceQueueResultDto
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string ReservationId { get; set; } = string.Empty;
    public int QueueId { get; set; }
    public string? ExternalReference { get; set; }
    public string Status { get; set; } = string.Empty;
    public TimeSpan? EstimatedProcessingTime { get; set; }
}

public class InvoiceQueueStatusDto
{
    public int QueueId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public string CustomerCode { get; set; } = string.Empty;
    /// <summary>The local sales order this invoice was converted from, if any.</summary>
    public int? SalesOrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; }
    public string? LastError { get; set; }
    public string? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public string? FiscalDeviceNumber { get; set; }
    public string? FiscalReceiptNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string? WarehouseCode { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int WaitTimeSeconds { get; set; }
    public bool IsComplete { get; set; }
    public bool IsFailed { get; set; }
    public bool CanRetry { get; set; }
    public bool CanCancel { get; set; }
}

public class InvoiceQueueStatsDto
{
    public int TotalQueued { get; set; }
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int RequiresReview { get; set; }
    public int Cancelled { get; set; }
    public int Fiscalized { get; set; }
    public DateTime? OldestPendingAge { get; set; }
    public decimal TotalAmountPending { get; set; }
    public double AverageWaitTimeSeconds { get; set; }
}

#endregion
