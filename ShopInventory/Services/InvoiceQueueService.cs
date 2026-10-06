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
    /// Hands a van sale the device has already been asked to sign to the queue — to be posted, or reviewed.
    /// </summary>
    /// <remarks>
    /// <para><b>Written in its final state, in one save.</b> <see cref="EnqueueInvoiceAsync"/> creates an
    /// entry as Pending, and InvoicePostingJob signs every Pending entry it finds that asks for it. Enqueueing
    /// and then marking the entry would leave a window in which the job signs the sale a second time, and a
    /// fiscal receipt cannot be withdrawn. So the entry is created already
    /// <see cref="InvoiceQueueStatus.Fiscalized"/> — which <c>PostQueuedVanInvoices</c> posts without
    /// fiscalising — or already <see cref="InvoiceQueueStatus.RequiresReview"/> when the device could not say
    /// whether it signed.</para>
    ///
    /// <para><b>It carries the receipt.</b> <c>PostQueuedVanInvoices</c> writes the entry's verification code
    /// and QR onto the invoice's <c>U_Fiscal_Code</c> / <c>U_Fiscal_Url</c>, so an entry written without them
    /// posts an invoice whose fiscal fields are empty in SAP.</para>
    ///
    /// <para><b>Always marked as started.</b> <c>ProcessingStartedAt</c> is what makes InvoicePostingJob ask
    /// the device for an existing receipt before signing, if a person puts the entry back with Retry.</para>
    ///
    /// <para><c>salesOrderId</c> is the order a conversion came from, so the invoice is posted on its base.
    /// Null for a direct sale.</para>
    ///
    /// <para>A failure is logged and answered with null, never thrown: the receipt is already in the
    /// customer's hand.</para>
    /// </remarks>
    Task<InvoiceQueueResultDto?> EnqueueSignedVanSaleAsync(
        CreateStockReservationRequest request,
        string reservationId,
        string createdBy,
        VanSaleFiscalFirstOutcome outcome,
        int? salesOrderId,
        CancellationToken cancellationToken);

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
        string? fiscalQrCode = null,
        string? fiscalVerificationCode = null,
        string? fiscalDayNo = null,
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

    public async Task<InvoiceQueueResultDto?> EnqueueSignedVanSaleAsync(
        CreateStockReservationRequest request,
        string reservationId,
        string createdBy,
        VanSaleFiscalFirstOutcome outcome,
        int? salesOrderId,
        CancellationToken cancellationToken)
    {
        var reference = request.GetExternalReference();

        try
        {
            var existing = await _context.InvoiceQueue
                .AsNoTracking()
                .FirstOrDefaultAsync(q => q.ExternalReference == reference, cancellationToken);

            if (existing is not null)
            {
                // A resend of a sale already handed over. The entry is already on its way.
                return new InvoiceQueueResultDto
                {
                    Success = true,
                    ReservationId = existing.ReservationId,
                    QueueId = existing.Id,
                    ExternalReference = existing.ExternalReference,
                    Status = existing.Status.ToString()
                };
            }

            var signed = outcome.Status == VanSaleFiscalFirstStatus.AwaitingSap;
            var sale = signed ? outcome.Sale : null;
            var now = DateTime.UtcNow;

            var entry = new InvoiceQueueEntity
            {
                ReservationId = reservationId,
                ExternalReference = reference,
                CustomerCode = request.CardCode,
                InvoicePayload = JsonSerializer.Serialize(
                    request,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                Status = signed ? InvoiceQueueStatus.Fiscalized : InvoiceQueueStatus.RequiresReview,
                SourceSystem = SaleSourceSystems.VanSales,
                WarehouseCode = request.Lines.FirstOrDefault()?.WarehouseCode,
                TotalAmount = request.Lines.Sum(l => l.Quantity * l.UnitPrice),
                Currency = request.Currency ?? "USD",
                RequiresFiscalization = true,
                FiscalizationSuccess = signed ? true : null,
                FiscalDeviceNumber = sale?.FiscalDeviceNumber,
                FiscalReceiptNumber = sale?.FiscalReceiptNumber,
                FiscalVerificationCode = sale?.FiscalVerificationCode,
                FiscalQrCode = sale?.FiscalQRCode,
                FiscalDayNo = sale?.FiscalDayNo,
                LastError = outcome.Error is { Length: > 2000 } tooLong ? tooLong[..2000] : outcome.Error,
                CreatedBy = createdBy,
                Notes = request.Notes,
                SalesOrderId = salesOrderId,
                CreatedAt = now,
                ProcessingStartedAt = now,
                ProcessedAt = signed ? null : now,
                MaxRetries = 3
            };

            _context.InvoiceQueue.Add(entry);
            await _context.SaveChangesAsync(cancellationToken);

            return new InvoiceQueueResultDto
            {
                Success = true,
                ReservationId = reservationId,
                QueueId = entry.Id,
                ExternalReference = reference,
                Status = entry.Status.ToString(),
                EstimatedProcessingTime = signed ? TimeSpan.FromSeconds(30) : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Van sale {Reference} was sent to the fiscal device but could not be queued for SAP posting. Its " +
                "receipt row holds no DocNum; resending the same van order will post it.",
                reference);

            return null;
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
        string? fiscalQrCode = null,
        string? fiscalVerificationCode = null,
        string? fiscalDayNo = null,
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

        if (!string.IsNullOrEmpty(fiscalQrCode))
        {
            entry.FiscalQrCode = fiscalQrCode;
        }

        if (!string.IsNullOrEmpty(fiscalVerificationCode))
        {
            entry.FiscalVerificationCode = fiscalVerificationCode;
        }

        if (!string.IsNullOrEmpty(fiscalDayNo))
        {
            entry.FiscalDayNo = fiscalDayNo;
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
