using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
    Task<InvoiceQueueResultDto> EnqueueInvoiceAsync(
        CreateStockReservationRequest request,
        string reservationId,
        string? createdBy = null,
        CancellationToken cancellationToken = default);

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
    /// Get queue statistics
    /// </summary>
    Task<InvoiceQueueStatsDto> GetQueueStatsAsync(CancellationToken cancellationToken = default);

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
}

public class InvoiceQueueService : IInvoiceQueueService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<InvoiceQueueService> _logger;

    public InvoiceQueueService(
        ApplicationDbContext context,
        ILogger<InvoiceQueueService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<InvoiceQueueResultDto> EnqueueInvoiceAsync(
        CreateStockReservationRequest request,
        string reservationId,
        string? createdBy = null,
        CancellationToken cancellationToken = default)
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

    public async Task<InvoiceQueueStatsDto> GetQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = await _context.InvoiceQueue
            .GroupBy(_ => 1)
            .Select(g => new InvoiceQueueStatsDto
            {
                TotalQueued = g.Count(),
                Pending = g.Count(q => q.Status == InvoiceQueueStatus.Pending),
                Processing = g.Count(q => q.Status == InvoiceQueueStatus.Processing),
                Completed = g.Count(q => q.Status == InvoiceQueueStatus.Completed),
                Failed = g.Count(q => q.Status == InvoiceQueueStatus.Failed),
                RequiresReview = g.Count(q => q.Status == InvoiceQueueStatus.RequiresReview),
                Cancelled = g.Count(q => q.Status == InvoiceQueueStatus.Cancelled),
                OldestPendingAge = g.Where(q => q.Status == InvoiceQueueStatus.Pending)
                                    .Min(q => (DateTime?)q.CreatedAt),
                TotalAmountPending = g.Where(q => q.Status == InvoiceQueueStatus.Pending)
                                      .Sum(q => q.TotalAmount)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return stats ?? new InvoiceQueueStatsDto();
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

        if (status == InvoiceQueueStatus.Completed || status == InvoiceQueueStatus.RequiresReview)
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
    public DateTime? OldestPendingAge { get; set; }
    public decimal TotalAmountPending { get; set; }
    public double AverageWaitTimeSeconds { get; set; }
}

#endregion
