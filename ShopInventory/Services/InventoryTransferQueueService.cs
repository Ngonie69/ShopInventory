using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Service for managing the inventory transfer posting queue
/// </summary>
public interface IInventoryTransferQueueService
{
    /// <summary>
    /// Enqueue an inventory transfer for batch posting to SAP
    /// </summary>
    Task<InventoryTransferQueueResultDto> EnqueueTransferAsync(
        CreateDesktopTransferRequest request,
        string? reservationId = null,
        string? createdBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the status of a queued transfer by external reference
    /// </summary>
    Task<InventoryTransferQueueStatusDto?> GetQueueStatusAsync(
        string externalReference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all pending/processing transfers in the queue
    /// </summary>
    Task<List<InventoryTransferQueueStatusDto>> GetPendingTransfersAsync(
        string? sourceSystem = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get transfers requiring manual review
    /// </summary>
    Task<List<InventoryTransferQueueStatusDto>> GetTransfersRequiringReviewAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a queued transfer (only if pending)
    /// </summary>
    Task<bool> CancelQueuedTransferAsync(
        string externalReference,
        string? cancelledBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retry a failed transfer
    /// </summary>
    Task<bool> RetryTransferAsync(
        string externalReference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get queue statistics
    /// </summary>
    Task<InventoryTransferQueueStatsDto> GetQueueStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Update queue entry after SAP posting
    /// </summary>
    Task UpdateQueueEntryAsync(
        int queueId,
        InventoryTransferQueueStatus status,
        string? sapDocEntry = null,
        int? sapDocNum = null,
        string? error = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get next batch of transfers to process
    /// </summary>
    Task<List<InventoryTransferQueueEntity>> GetNextBatchForProcessingAsync(
        int batchSize = 5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a transfer as processing
    /// </summary>
    Task MarkAsProcessingAsync(int queueId, CancellationToken cancellationToken = default);
}

public class InventoryTransferQueueService : IInventoryTransferQueueService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<InventoryTransferQueueService> _logger;

    public InventoryTransferQueueService(
        ApplicationDbContext context,
        ILogger<InventoryTransferQueueService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<InventoryTransferQueueResultDto> EnqueueTransferAsync(
        CreateDesktopTransferRequest request,
        string? reservationId = null,
        string? createdBy = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var externalRef = request.ExternalReference ??
                $"DESKTOP-TRF-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8]}";

            // Check if already queued
            var existing = await _context.InventoryTransferQueue
                .FirstOrDefaultAsync(q => q.ExternalReference == externalRef, cancellationToken);

            if (existing != null)
            {
                return new InventoryTransferQueueResultDto
                {
                    Success = false,
                    ErrorCode = "ALREADY_QUEUED",
                    ErrorMessage = $"Transfer with reference '{externalRef}' is already queued",
                    QueueId = existing.Id,
                    Status = existing.Status.ToString()
                };
            }

            // Calculate totals
            decimal totalQuantity = request.Lines.Sum(l => l.Quantity);
            int lineCount = request.Lines.Count;

            var queueEntry = new InventoryTransferQueueEntity
            {
                ExternalReference = externalRef,
                FromWarehouse = request.FromWarehouse,
                ToWarehouse = request.ToWarehouse,
                TransferPayload = JsonSerializer.Serialize(request, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }),
                Status = InventoryTransferQueueStatus.Pending,
                SourceSystem = request.SourceSystem ?? "Desktop",
                TotalQuantity = totalQuantity,
                LineCount = lineCount,
                Priority = request.Priority ?? 0,
                CreatedBy = createdBy,
                Comments = request.Comments,
                JournalMemo = request.JournalMemo,
                DueDate = string.IsNullOrEmpty(request.DueDate) ? null : DateTime.Parse(request.DueDate),
                IsTransferRequest = request.IsTransferRequest,
                ReservationId = reservationId,
                CreatedAt = DateTime.UtcNow,
                MaxRetries = 3
            };

            _context.InventoryTransferQueue.Add(queueEntry);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Inventory transfer queued successfully: ExternalRef={ExternalReference}, QueueId={QueueId}, From={From}, To={To}",
                externalRef, queueEntry.Id, request.FromWarehouse, request.ToWarehouse);

            return new InventoryTransferQueueResultDto
            {
                Success = true,
                QueueId = queueEntry.Id,
                ExternalReference = externalRef,
                Status = InventoryTransferQueueStatus.Pending.ToString(),
                EstimatedProcessingTime = TimeSpan.FromSeconds(30)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue inventory transfer: {ExternalReference}", request.ExternalReference);
            return new InventoryTransferQueueResultDto
            {
                Success = false,
                ErrorCode = "QUEUE_ERROR",
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<InventoryTransferQueueStatusDto?> GetQueueStatusAsync(
        string externalReference,
        CancellationToken cancellationToken = default)
    {
        var entry = await _context.InventoryTransferQueue
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.ExternalReference == externalReference, cancellationToken);

        return entry == null ? null : MapToStatusDto(entry);
    }

    public async Task<List<InventoryTransferQueueStatusDto>> GetPendingTransfersAsync(
        string? sourceSystem = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var query = _context.InventoryTransferQueue
            .AsNoTracking()
            .Where(q => q.Status == InventoryTransferQueueStatus.Pending ||
                       q.Status == InventoryTransferQueueStatus.Processing ||
                       q.Status == InventoryTransferQueueStatus.Failed);

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

    public async Task<List<InventoryTransferQueueStatusDto>> GetTransfersRequiringReviewAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var entries = await _context.InventoryTransferQueue
            .AsNoTracking()
            .Where(q => q.Status == InventoryTransferQueueStatus.RequiresReview)
            .OrderBy(q => q.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return entries.Select(MapToStatusDto).ToList();
    }

    public async Task<bool> CancelQueuedTransferAsync(
        string externalReference,
        string? cancelledBy = null,
        CancellationToken cancellationToken = default)
    {
        var entry = await _context.InventoryTransferQueue
            .FirstOrDefaultAsync(q => q.ExternalReference == externalReference, cancellationToken);

        if (entry == null || entry.Status != InventoryTransferQueueStatus.Pending)
        {
            return false;
        }

        entry.Status = InventoryTransferQueueStatus.Cancelled;
        entry.ProcessedAt = DateTime.UtcNow;
        entry.LastError = $"Cancelled by {cancelledBy ?? "user"}";

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Queued transfer cancelled: {ExternalReference}", externalReference);
        return true;
    }

    public async Task<bool> RetryTransferAsync(
        string externalReference,
        CancellationToken cancellationToken = default)
    {
        var entry = await _context.InventoryTransferQueue
            .FirstOrDefaultAsync(q => q.ExternalReference == externalReference, cancellationToken);

        if (entry == null)
        {
            return false;
        }

        if (entry.Status != InventoryTransferQueueStatus.Failed &&
            entry.Status != InventoryTransferQueueStatus.RequiresReview)
        {
            return false;
        }

        entry.Status = InventoryTransferQueueStatus.Pending;
        entry.RetryCount = 0; // Reset retry count for manual retry
        entry.NextRetryAt = null;
        entry.ProcessingStartedAt = null;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Queued transfer marked for retry: {ExternalReference}", externalReference);
        return true;
    }

    public async Task<InventoryTransferQueueStatsDto> GetQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = await _context.InventoryTransferQueue
            .AsNoTracking()
            .GroupBy(q => 1)
            .Select(g => new InventoryTransferQueueStatsDto
            {
                TotalQueued = g.Count(),
                Pending = g.Count(q => q.Status == InventoryTransferQueueStatus.Pending),
                Processing = g.Count(q => q.Status == InventoryTransferQueueStatus.Processing),
                Completed = g.Count(q => q.Status == InventoryTransferQueueStatus.Completed),
                Failed = g.Count(q => q.Status == InventoryTransferQueueStatus.Failed),
                RequiresReview = g.Count(q => q.Status == InventoryTransferQueueStatus.RequiresReview),
                Cancelled = g.Count(q => q.Status == InventoryTransferQueueStatus.Cancelled)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (stats == null)
        {
            return new InventoryTransferQueueStatsDto();
        }

        // Get oldest pending
        var oldestPending = await _context.InventoryTransferQueue
            .AsNoTracking()
            .Where(q => q.Status == InventoryTransferQueueStatus.Pending)
            .OrderBy(q => q.CreatedAt)
            .Select(q => q.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        stats.OldestPendingAge = oldestPending != default ? oldestPending : null;

        // Get total quantity pending
        stats.TotalQuantityPending = await _context.InventoryTransferQueue
            .AsNoTracking()
            .Where(q => q.Status == InventoryTransferQueueStatus.Pending)
            .SumAsync(q => q.TotalQuantity, cancellationToken);

        return stats;
    }

    public async Task UpdateQueueEntryAsync(
        int queueId,
        InventoryTransferQueueStatus status,
        string? sapDocEntry = null,
        int? sapDocNum = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        var entry = await _context.InventoryTransferQueue
            .FirstOrDefaultAsync(q => q.Id == queueId, cancellationToken);

        if (entry == null)
        {
            _logger.LogWarning("Transfer queue entry not found: {QueueId}", queueId);
            return;
        }

        entry.Status = status;
        entry.SapDocEntry = sapDocEntry ?? entry.SapDocEntry;
        entry.SapDocNum = sapDocNum ?? entry.SapDocNum;
        entry.LastError = error ?? entry.LastError;
        entry.ProcessedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<InventoryTransferQueueEntity>> GetNextBatchForProcessingAsync(
        int batchSize = 5,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Get pending items, or failed items that are ready for retry
        var entries = await _context.InventoryTransferQueue
            .Where(q =>
                q.Status == InventoryTransferQueueStatus.Pending ||
                (q.Status == InventoryTransferQueueStatus.Failed &&
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
        var entry = await _context.InventoryTransferQueue
            .FirstOrDefaultAsync(q => q.Id == queueId, cancellationToken);

        if (entry == null) return;

        entry.Status = InventoryTransferQueueStatus.Processing;
        entry.ProcessingStartedAt = DateTime.UtcNow;
        entry.RetryCount++;

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static InventoryTransferQueueStatusDto MapToStatusDto(InventoryTransferQueueEntity entry)
    {
        var isComplete = entry.Status == InventoryTransferQueueStatus.Completed;
        var isFailed = entry.Status == InventoryTransferQueueStatus.Failed ||
                       entry.Status == InventoryTransferQueueStatus.RequiresReview;
        var canRetry = entry.Status == InventoryTransferQueueStatus.Failed ||
                       entry.Status == InventoryTransferQueueStatus.RequiresReview;
        var canCancel = entry.Status == InventoryTransferQueueStatus.Pending;

        return new InventoryTransferQueueStatusDto
        {
            QueueId = entry.Id,
            ExternalReference = entry.ExternalReference,
            FromWarehouse = entry.FromWarehouse,
            ToWarehouse = entry.ToWarehouse,
            Status = entry.Status.ToString(),
            StatusCode = (int)entry.Status,
            RetryCount = entry.RetryCount,
            MaxRetries = entry.MaxRetries,
            LastError = entry.LastError,
            SapDocEntry = entry.SapDocEntry,
            SapDocNum = entry.SapDocNum,
            CreatedAt = entry.CreatedAt,
            ProcessingStartedAt = entry.ProcessingStartedAt,
            ProcessedAt = entry.ProcessedAt,
            NextRetryAt = entry.NextRetryAt,
            SourceSystem = entry.SourceSystem,
            TotalQuantity = entry.TotalQuantity,
            LineCount = entry.LineCount,
            IsTransferRequest = entry.IsTransferRequest,
            WaitTimeSeconds = (int)(DateTime.UtcNow - entry.CreatedAt).TotalSeconds,
            IsComplete = isComplete,
            IsFailed = isFailed,
            CanRetry = canRetry,
            CanCancel = canCancel
        };
    }
}

#region DTOs for Inventory Transfer Queue

/// <summary>
/// Result of enqueueing an inventory transfer
/// </summary>
public class InventoryTransferQueueResultDto
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int QueueId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public TimeSpan? EstimatedProcessingTime { get; set; }
}

/// <summary>
/// Status of a queued inventory transfer
/// </summary>
public class InventoryTransferQueueStatusDto
{
    public int QueueId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string FromWarehouse { get; set; } = string.Empty;
    public string ToWarehouse { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; }
    public string? LastError { get; set; }
    public string? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public decimal TotalQuantity { get; set; }
    public int LineCount { get; set; }
    public bool IsTransferRequest { get; set; }
    public int WaitTimeSeconds { get; set; }
    public bool IsComplete { get; set; }
    public bool IsFailed { get; set; }
    public bool CanRetry { get; set; }
    public bool CanCancel { get; set; }
}

/// <summary>
/// Queue statistics for inventory transfers
/// </summary>
public class InventoryTransferQueueStatsDto
{
    public int TotalQueued { get; set; }
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int RequiresReview { get; set; }
    public int Cancelled { get; set; }
    public DateTime? OldestPendingAge { get; set; }
    public decimal TotalQuantityPending { get; set; }
}

/// <summary>
/// Request to create a desktop inventory transfer
/// </summary>
public class CreateDesktopTransferRequest
{
    public string? ExternalReference { get; set; }
    public string? SourceSystem { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "From warehouse is required")]
    public string FromWarehouse { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "To warehouse is required")]
    public string ToWarehouse { get; set; } = string.Empty;

    public string? DocDate { get; set; }
    public string? DueDate { get; set; }
    public string? Comments { get; set; }
    public string? JournalMemo { get; set; }
    public int? Priority { get; set; }

    /// <summary>
    /// If true, creates a Transfer Request (requires approval). If false, creates a direct Transfer.
    /// </summary>
    public bool IsTransferRequest { get; set; } = true;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "At least one line item is required")]
    [System.ComponentModel.DataAnnotations.MinLength(1)]
    public List<CreateDesktopTransferLineRequest> Lines { get; set; } = new();
}

/// <summary>
/// Line item for desktop inventory transfer
/// </summary>
public class CreateDesktopTransferLineRequest
{
    public int LineNum { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = string.Empty;

    public string? ItemDescription { get; set; }

    [System.ComponentModel.DataAnnotations.Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    public string? UoMCode { get; set; }

    /// <summary>
    /// Optional: specific from warehouse for this line (overrides header FromWarehouse)
    /// </summary>
    public string? FromWarehouseCode { get; set; }

    /// <summary>
    /// Optional: specific to warehouse for this line (overrides header ToWarehouse)
    /// </summary>
    public string? WarehouseCode { get; set; }

    public bool AutoAllocateBatches { get; set; } = true;
    public List<TransferBatchRequest>? BatchNumbers { get; set; }
}

// Note: TransferBatchRequest is defined in StockValidationService.cs

#endregion
