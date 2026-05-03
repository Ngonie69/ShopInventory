using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;

public sealed class GetExceptionCenterHandler(
    ApplicationDbContext context,
    ILogger<GetExceptionCenterHandler> logger
) : IRequestHandler<GetExceptionCenterQuery, ErrorOr<ExceptionCenterDashboardDto>>
{
    private const int DefaultPerSourceLimit = 40;
    private const string InvoiceQueueSource = "invoice-queue";
    private const string TransferQueueSource = "inventory-transfer-queue";
    private const string MobileQueueSource = "mobile-order-post-processing";
    private const string PaymentSource = "payment-callback";
    private const string PaymentRejectedSource = "payment-callback-rejection";
    private const string CreditNoteFiscalizationSource = "credit-note-fiscalization";

    public async Task<ErrorOr<ExceptionCenterDashboardDto>> Handle(
        GetExceptionCenterQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var perSourceLimit = Math.Clamp(request.Limit <= 0 ? DefaultPerSourceLimit : request.Limit, 20, 200);

            var invoiceItems = await context.InvoiceQueue
                .AsNoTracking()
                .Where(q => q.Status == InvoiceQueueStatus.Failed || q.Status == InvoiceQueueStatus.RequiresReview)
                .OrderByDescending(q => q.Status == InvoiceQueueStatus.RequiresReview)
                .ThenByDescending(q => q.ProcessedAt ?? q.CreatedAt)
                .Take(perSourceLimit)
                .Select(q => new ExceptionCenterItemDto
                {
                    Source = InvoiceQueueSource,
                    ItemId = q.Id,
                    Category = q.LastError != null && EF.Functions.ILike(q.LastError, "%fiscalization%")
                        ? "REVMax"
                        : "SAP Posting",
                    Title = q.LastError != null && EF.Functions.ILike(q.LastError, "%fiscalization%")
                        ? "Invoice fiscalization issue"
                        : "Invoice posting issue",
                    Reference = q.ExternalReference,
                    Status = q.Status.ToString(),
                    SourceSystem = q.SourceSystem,
                    LastError = q.LastError,
                    RetryCount = q.RetryCount,
                    MaxRetries = q.MaxRetries,
                    CreatedAtUtc = q.CreatedAt,
                    OccurredAtUtc = q.ProcessedAt ?? q.ProcessingStartedAt ?? q.CreatedAt,
                    NextRetryAtUtc = q.NextRetryAt,
                    CanRetry = q.Status == InvoiceQueueStatus.Failed || q.Status == InvoiceQueueStatus.RequiresReview
                })
                .ToListAsync(cancellationToken);

            var transferItems = await context.InventoryTransferQueue
                .AsNoTracking()
                .Where(q => q.Status == InventoryTransferQueueStatus.Failed || q.Status == InventoryTransferQueueStatus.RequiresReview)
                .OrderByDescending(q => q.Status == InventoryTransferQueueStatus.RequiresReview)
                .ThenByDescending(q => q.ProcessedAt ?? q.CreatedAt)
                .Take(perSourceLimit)
                .Select(q => new ExceptionCenterItemDto
                {
                    Source = TransferQueueSource,
                    ItemId = q.Id,
                    Category = "SAP Posting",
                    Title = q.IsTransferRequest ? "Transfer request posting issue" : "Inventory transfer posting issue",
                    Reference = q.ExternalReference,
                    Status = q.Status.ToString(),
                    SourceSystem = q.SourceSystem,
                    LastError = q.LastError,
                    RetryCount = q.RetryCount,
                    MaxRetries = q.MaxRetries,
                    CreatedAtUtc = q.CreatedAt,
                    OccurredAtUtc = q.ProcessedAt ?? q.ProcessingStartedAt ?? q.CreatedAt,
                    NextRetryAtUtc = q.NextRetryAt,
                    CanRetry = q.Status == InventoryTransferQueueStatus.Failed || q.Status == InventoryTransferQueueStatus.RequiresReview
                })
                .ToListAsync(cancellationToken);

            var mobileItems = await context.MobileOrderPostProcessingQueue
                .AsNoTracking()
                .Where(q => q.Status == MobileOrderPostProcessingQueueStatus.Failed || q.Status == MobileOrderPostProcessingQueueStatus.RequiresReview)
                .OrderByDescending(q => q.Status == MobileOrderPostProcessingQueueStatus.RequiresReview)
                .ThenByDescending(q => q.ProcessedAt ?? q.CreatedAt)
                .Take(perSourceLimit)
                .Select(q => new ExceptionCenterItemDto
                {
                    Source = MobileQueueSource,
                    ItemId = q.Id,
                    Category = "Sync Retry",
                    Title = "Mobile order post-processing issue",
                    Reference = q.OrderNumber,
                    Status = q.Status.ToString(),
                    SourceSystem = "Mobile",
                    LastError = q.LastError,
                    RetryCount = q.RetryCount,
                    MaxRetries = q.MaxRetries,
                    CreatedAtUtc = q.CreatedAt,
                    OccurredAtUtc = q.ProcessedAt ?? q.ProcessingStartedAt ?? q.CreatedAt,
                    NextRetryAtUtc = q.NextRetryAt,
                    CanRetry = q.Status == MobileOrderPostProcessingQueueStatus.Failed || q.Status == MobileOrderPostProcessingQueueStatus.RequiresReview
                })
                .ToListAsync(cancellationToken);

            var paymentItems = await context.PaymentTransactions
                .AsNoTracking()
                .Where(t => t.Status == PaymentStatus.Failed)
                .OrderByDescending(t => t.UpdatedAt ?? t.CompletedAt ?? t.CreatedAt)
                .Take(perSourceLimit)
                .Select(t => new ExceptionCenterItemDto
                {
                    Source = PaymentSource,
                    ItemId = t.Id,
                    Category = "Payment Callback",
                    Title = $"{t.Provider} callback or settlement issue",
                    Reference = t.Reference ?? t.ExternalTransactionId ?? $"Payment #{t.Id}",
                    Status = t.Status,
                    Provider = t.Provider,
                    LastError = t.StatusMessage,
                    RetryCount = 0,
                    MaxRetries = 0,
                    CreatedAtUtc = t.CreatedAt,
                    OccurredAtUtc = t.UpdatedAt ?? t.CompletedAt ?? t.CreatedAt,
                    NextRetryAtUtc = null,
                    CanRetry = false
                })
                .ToListAsync(cancellationToken);

            var incidentItems = await context.ExceptionCenterIncidents
                .AsNoTracking()
                .Where(i => i.Source == PaymentRejectedSource || i.Source == CreditNoteFiscalizationSource)
                .OrderByDescending(i => i.Status == "RequiresReview")
                .ThenByDescending(i => i.OccurredAtUtc ?? i.CreatedAtUtc)
                .Take(perSourceLimit)
                .Select(i => new ExceptionCenterItemDto
                {
                    Source = i.Source,
                    ItemId = i.Id,
                    Category = i.Category,
                    Title = i.Title,
                    Reference = i.Reference,
                    Status = i.Status,
                    SourceSystem = i.SourceSystem,
                    Provider = i.Provider,
                    LastError = i.LastError,
                    RetryCount = i.RetryCount,
                    MaxRetries = i.MaxRetries,
                    CreatedAtUtc = i.CreatedAtUtc,
                    OccurredAtUtc = i.OccurredAtUtc ?? i.CreatedAtUtc,
                    NextRetryAtUtc = i.NextRetryAtUtc,
                    CanRetry = i.CanRetry
                })
                .ToListAsync(cancellationToken);

            var items = invoiceItems
                .Concat(transferItems)
                .Concat(mobileItems)
                .Concat(paymentItems)
                .Concat(incidentItems)
                .ToList();

            var stateSources = items.Select(item => item.Source).Distinct().ToList();
            var stateItemIds = items.Select(item => item.ItemId).Distinct().ToList();

            var states = await context.ExceptionCenterItemStates
                .AsNoTracking()
                .Where(state => stateSources.Contains(state.Source) && stateItemIds.Contains(state.ItemId))
                .ToListAsync(cancellationToken);

            var stateMap = states.ToDictionary(
                state => BuildStateKey(state.Source, state.ItemId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (!stateMap.TryGetValue(BuildStateKey(item.Source, item.ItemId), out var state))
                {
                    continue;
                }

                item.IsAcknowledged = state.IsAcknowledged;
                item.AcknowledgedAtUtc = state.AcknowledgedAtUtc;
                item.AcknowledgedByUsername = state.AcknowledgedByUsername;
                item.AssignedToUsername = state.AssignedToUsername;
                item.AssignedAtUtc = state.AssignedAtUtc;
            }

            items = items
                .OrderBy(item => item.IsAcknowledged)
                .ThenBy(item => !string.IsNullOrWhiteSpace(item.AssignedToUsername))
                .ThenByDescending(item => GetStatusRank(item.Status))
                .ThenByDescending(item => item.OccurredAtUtc ?? item.CreatedAtUtc)
                .Take(request.Limit <= 0 ? 100 : request.Limit)
                .ToList();

            return new ExceptionCenterDashboardDto
            {
                OpenCount = items.Count,
                RequiresReviewCount = items.Count(item => string.Equals(item.Status, "RequiresReview", StringComparison.OrdinalIgnoreCase)),
                RetryScheduledCount = items.Count(item => item.NextRetryAtUtc.HasValue),
                SapIssueCount = items.Count(item => string.Equals(item.Category, "SAP Posting", StringComparison.OrdinalIgnoreCase)),
                RevmaxIssueCount = items.Count(item => string.Equals(item.Category, "REVMax", StringComparison.OrdinalIgnoreCase)),
                SyncIssueCount = items.Count(item => string.Equals(item.Category, "Sync Retry", StringComparison.OrdinalIgnoreCase)),
                PaymentIssueCount = items.Count(item => string.Equals(item.Category, "Payment Callback", StringComparison.OrdinalIgnoreCase)),
                Items = items
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load exception center dashboard");
            return Errors.ExceptionCenter.LoadFailed("Failed to load exception center dashboard.");
        }
    }

    private static int GetStatusRank(string status)
        => status switch
        {
            "RequiresReview" => 3,
            "Failed" => 2,
            _ => 1
        };

    private static string BuildStateKey(string source, int itemId) => $"{source}:{itemId}";
}