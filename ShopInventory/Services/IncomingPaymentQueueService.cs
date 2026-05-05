using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Validation;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

public interface IIncomingPaymentQueueService
{
    Task<IncomingPaymentQueueResultDto> EnqueuePaymentAsync(
        CreateIncomingPaymentRequest request,
        string? createdBy = null,
        CancellationToken cancellationToken = default);

    Task<IncomingPaymentQueueStatusDto?> GetQueueStatusAsync(
        string externalReference,
        CancellationToken cancellationToken = default);

    Task UpdateQueueEntryAsync(
        int queueId,
        IncomingPaymentQueueStatus status,
        string? sapDocEntry = null,
        int? sapDocNum = null,
        string? error = null,
        CancellationToken cancellationToken = default);

    Task<List<IncomingPaymentQueueEntity>> GetNextBatchForProcessingAsync(
        int batchSize = 5,
        CancellationToken cancellationToken = default);

    Task MarkAsProcessingAsync(int queueId, CancellationToken cancellationToken = default);
}

public sealed class IncomingPaymentQueueService(
    ApplicationDbContext context,
    ILogger<IncomingPaymentQueueService> logger) : IIncomingPaymentQueueService
{
    public async Task<IncomingPaymentQueueResultDto> EnqueuePaymentAsync(
        CreateIncomingPaymentRequest request,
        string? createdBy = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationErrors = RecursiveDataAnnotationsValidator.Validate(request);
            validationErrors.AddRange(ValidatePaymentAmounts(request));

            if (validationErrors.Count > 0)
            {
                return new IncomingPaymentQueueResultDto
                {
                    Success = false,
                    ErrorCode = "VALIDATION_ERROR",
                    ErrorMessage = $"Incoming payment validation failed: {string.Join("; ", validationErrors)}"
                };
            }

            var externalReference = BuildExternalReference(request);
            var queueEntry = new IncomingPaymentQueueEntity
            {
                ExternalReference = externalReference,
                CustomerCode = request.CardCode!.Trim(),
                PaymentPayload = JsonSerializer.Serialize(request, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }),
                Status = IncomingPaymentQueueStatus.Pending,
                SourceSystem = "API",
                Priority = 0,
                TotalAmount = request.CashSum + request.TransferSum + request.CheckSum + request.CreditSum,
                Currency = "USD",
                CreatedBy = createdBy,
                Remarks = request.Remarks,
                CreatedAt = DateTime.UtcNow,
                MaxRetries = 3
            };

            context.IncomingPaymentQueue.Add(queueEntry);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Incoming payment queued successfully: ExternalRef={ExternalReference}, QueueId={QueueId}, Customer={CustomerCode}, Total={TotalAmount}",
                externalReference,
                queueEntry.Id,
                queueEntry.CustomerCode,
                queueEntry.TotalAmount);

            return new IncomingPaymentQueueResultDto
            {
                Success = true,
                QueueId = queueEntry.Id,
                ExternalReference = externalReference,
                Status = queueEntry.Status.ToString(),
                EstimatedProcessingTime = TimeSpan.FromSeconds(30)
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enqueue incoming payment for {CardCode}", request.CardCode);
            return new IncomingPaymentQueueResultDto
            {
                Success = false,
                ErrorCode = "QUEUE_ERROR",
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<IncomingPaymentQueueStatusDto?> GetQueueStatusAsync(
        string externalReference,
        CancellationToken cancellationToken = default)
    {
        var entry = await context.IncomingPaymentQueue
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.ExternalReference == externalReference, cancellationToken);

        return entry == null ? null : MapToStatusDto(entry);
    }

    public async Task UpdateQueueEntryAsync(
        int queueId,
        IncomingPaymentQueueStatus status,
        string? sapDocEntry = null,
        int? sapDocNum = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        var entry = await context.IncomingPaymentQueue
            .AsTracking()
            .FirstOrDefaultAsync(q => q.Id == queueId, cancellationToken);

        if (entry == null)
        {
            logger.LogWarning("Incoming payment queue entry {QueueId} not found while updating status to {Status}", queueId, status);
            return;
        }

        entry.Status = status;
        entry.SapDocEntry = sapDocEntry;
        entry.SapDocNum = sapDocNum;
        entry.LastError = error;
        entry.ProcessedAt = status is IncomingPaymentQueueStatus.Completed or IncomingPaymentQueueStatus.RequiresReview or IncomingPaymentQueueStatus.Cancelled
            ? DateTime.UtcNow
            : null;
        entry.NextRetryAt = status == IncomingPaymentQueueStatus.Failed
            ? DateTime.UtcNow.AddSeconds(Math.Min(300, Math.Max(30, entry.RetryCount * 30)))
            : null;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<IncomingPaymentQueueEntity>> GetNextBatchForProcessingAsync(
        int batchSize = 5,
        CancellationToken cancellationToken = default)
    {
        var utcNow = DateTime.UtcNow;

        return await context.IncomingPaymentQueue
            .AsNoTracking()
            .Where(q => q.Status == IncomingPaymentQueueStatus.Pending ||
                        (q.Status == IncomingPaymentQueueStatus.Failed && q.NextRetryAt != null && q.NextRetryAt <= utcNow))
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task MarkAsProcessingAsync(int queueId, CancellationToken cancellationToken = default)
    {
        var entry = await context.IncomingPaymentQueue
            .AsTracking()
            .FirstOrDefaultAsync(q => q.Id == queueId, cancellationToken);

        if (entry == null)
        {
            logger.LogWarning("Incoming payment queue entry {QueueId} not found while marking processing", queueId);
            return;
        }

        entry.Status = IncomingPaymentQueueStatus.Processing;
        entry.ProcessingStartedAt = DateTime.UtcNow;
        entry.RetryCount += 1;
        entry.LastError = null;
        entry.NextRetryAt = null;

        await context.SaveChangesAsync(cancellationToken);
    }

    private static string BuildExternalReference(CreateIncomingPaymentRequest request)
    {
        var customerSegment = string.IsNullOrWhiteSpace(request.CardCode)
            ? "UNKNOWN"
            : new string(request.CardCode.Trim().Where(char.IsLetterOrDigit).ToArray());

        if (string.IsNullOrWhiteSpace(customerSegment))
        {
            customerSegment = "UNKNOWN";
        }

        return $"API-PAY-{customerSegment}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8]}";
    }

    private static List<string> ValidatePaymentAmounts(CreateIncomingPaymentRequest request)
    {
        var errors = new List<string>();

        if (request.CashSum < 0) errors.Add($"Cash sum cannot be negative. Current value: {request.CashSum}");
        if (request.TransferSum < 0) errors.Add($"Transfer sum cannot be negative. Current value: {request.TransferSum}");
        if (request.CheckSum < 0) errors.Add($"Check sum cannot be negative. Current value: {request.CheckSum}");
        if (request.CreditSum < 0) errors.Add($"Credit sum cannot be negative. Current value: {request.CreditSum}");

        var totalPayment = request.CashSum + request.TransferSum + request.CheckSum + request.CreditSum;
        if (totalPayment <= 0) errors.Add("At least one payment amount must be greater than zero");

        if (request.PaymentInvoices != null)
        {
            for (int i = 0; i < request.PaymentInvoices.Count; i++)
            {
                if (request.PaymentInvoices[i].SumApplied < 0)
                {
                    errors.Add($"Invoice {i + 1}: Sum applied cannot be negative. Current value: {request.PaymentInvoices[i].SumApplied}");
                }
            }
        }

        if (request.PaymentChecks != null)
        {
            for (int i = 0; i < request.PaymentChecks.Count; i++)
            {
                if (request.PaymentChecks[i].CheckSum < 0)
                {
                    errors.Add($"Check {i + 1}: Check sum cannot be negative. Current value: {request.PaymentChecks[i].CheckSum}");
                }
            }
        }

        if (request.PaymentCreditCards != null)
        {
            for (int i = 0; i < request.PaymentCreditCards.Count; i++)
            {
                if (request.PaymentCreditCards[i].CreditSum < 0)
                {
                    errors.Add($"Credit card {i + 1}: Credit sum cannot be negative. Current value: {request.PaymentCreditCards[i].CreditSum}");
                }
            }
        }

        return errors;
    }

    private static IncomingPaymentQueueStatusDto MapToStatusDto(IncomingPaymentQueueEntity entry)
    {
        var isFailed = entry.Status is IncomingPaymentQueueStatus.Failed or IncomingPaymentQueueStatus.RequiresReview;
        var isComplete = entry.Status == IncomingPaymentQueueStatus.Completed;

        return new IncomingPaymentQueueStatusDto
        {
            QueueId = entry.Id,
            ExternalReference = entry.ExternalReference,
            CustomerCode = entry.CustomerCode,
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
            TotalAmount = entry.TotalAmount,
            Currency = entry.Currency,
            WaitTimeSeconds = (int)(DateTime.UtcNow - entry.CreatedAt).TotalSeconds,
            IsComplete = isComplete,
            IsFailed = isFailed,
            CanRetry = entry.Status == IncomingPaymentQueueStatus.Failed && entry.RetryCount < entry.MaxRetries,
            CanCancel = entry.Status == IncomingPaymentQueueStatus.Pending
        };
    }
}

public sealed class IncomingPaymentQueueResultDto
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int QueueId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public TimeSpan? EstimatedProcessingTime { get; set; }
}

public sealed class IncomingPaymentQueueStatusDto
{
    public int QueueId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string CustomerCode { get; set; } = string.Empty;
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
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int WaitTimeSeconds { get; set; }
    public bool IsComplete { get; set; }
    public bool IsFailed { get; set; }
    public bool CanRetry { get; set; }
    public bool CanCancel { get; set; }
}