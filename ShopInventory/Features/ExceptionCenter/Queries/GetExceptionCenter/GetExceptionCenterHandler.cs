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
    private const string InvoiceQueueSource = ExceptionCenterSources.InvoiceQueue;
    private const string TransferQueueSource = ExceptionCenterSources.InventoryTransferQueue;
    private const string MobileQueueSource = ExceptionCenterSources.MobileOrderPostProcessing;
    private const string PaymentSource = ExceptionCenterSources.PaymentCallback;
    private const string PaymentRejectedSource = ExceptionCenterSources.PaymentCallbackRejection;
    private const string CreditNoteFiscalizationSource = ExceptionCenterSources.CreditNoteFiscalization;

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

            var pendingTransferItems = await LoadPendingTransferPostFailuresAsync(context, perSourceLimit, cancellationToken);
            var pendingEditItems = await LoadPendingRequestEditApplyFailuresAsync(context, perSourceLimit, cancellationToken);

            var items = invoiceItems
                .Concat(transferItems)
                .Concat(mobileItems)
                .Concat(paymentItems)
                .Concat(incidentItems)
                .Concat(pendingTransferItems)
                .Concat(pendingEditItems)
                .ToList();

            EnsureItemKeys(items);

            var stateSources = items.Select(item => item.Source).Distinct().ToList();
            var stateItemKeys = items.Select(item => item.ItemKey).Distinct().ToList();

            var states = await context.ExceptionCenterItemStates
                .AsNoTracking()
                .Where(state => stateSources.Contains(state.Source)
                                && state.ItemKey != null
                                && stateItemKeys.Contains(state.ItemKey))
                .ToListAsync(cancellationToken);

            ApplyStates(items, states);

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

    /// <summary>
    /// Fills in the routing key for the int-keyed sources, which project their id straight from
    /// the database. That key is the id in decimal — exactly what those items were addressed by
    /// before Guid-keyed sources arrived. The Guid-keyed loaders set their own.
    /// </summary>
    internal static void EnsureItemKeys(List<ExceptionCenterItemDto> items)
    {
        foreach (var item in items.Where(item => string.IsNullOrEmpty(item.ItemKey)))
        {
            item.ItemKey = ExceptionCenterSources.Key(item.ItemId);
        }
    }

    /// <summary>
    /// Lays each item's stored acknowledgement and assignment over it, matched on source and
    /// routing key. Requires <see cref="EnsureItemKeys"/> to have run.
    /// </summary>
    internal static void ApplyStates(
        List<ExceptionCenterItemDto> items,
        IReadOnlyCollection<ExceptionCenterItemStateEntity> states)
    {
        var stateMap = states
            .Where(state => state.ItemKey != null)
            .ToDictionary(
                state => BuildStateKey(state.Source, state.ItemKey!),
                StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (!stateMap.TryGetValue(BuildStateKey(item.Source, item.ItemKey), out var state))
            {
                continue;
            }

            item.IsAcknowledged = state.IsAcknowledged;
            item.AcknowledgedAtUtc = state.AcknowledgedAtUtc;
            item.AcknowledgedByUsername = state.AcknowledgedByUsername;
            item.AssignedToUsername = state.AssignedToUsername;
            item.AssignedAtUtc = state.AssignedAtUtc;
        }
    }

    /// <summary>
    /// Direct transfers that cleared every approval stage and then failed to post to SAP. They
    /// are Guid keyed, and nothing retries them on a timer, so until someone presses retry the
    /// stock never moves and no queue row exists to say so.
    /// </summary>
    internal static async Task<List<ExceptionCenterItemDto>> LoadPendingTransferPostFailuresAsync(
        ApplicationDbContext context,
        int perSourceLimit,
        CancellationToken cancellationToken)
    {
        // Projected to an anonymous shape first: the routing key is a formatted Guid, which is
        // built here rather than asked of the database.
        var rows = await context.PendingInventoryTransfers
            .AsNoTracking()
            .Where(p => p.Status == PendingInventoryTransferStatuses.PostFailed)
            .OrderByDescending(p => p.DecidedAtUtc ?? p.CreatedAtUtc)
            .Take(perSourceLimit)
            .Select(p => new
            {
                p.Id,
                p.DraftNumber,
                p.FromWarehouse,
                p.ToWarehouse,
                p.LineCount,
                p.TotalQuantity,
                p.CreatedByName,
                p.LastError,
                p.CreatedAtUtc,
                p.DecidedAtUtc
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(p => new ExceptionCenterItemDto
            {
                Source = ExceptionCenterSources.PendingInventoryTransferPost,
                ItemId = 0,
                ItemKey = ExceptionCenterSources.Key(p.Id),
                Category = "SAP Posting",
                Title = $"Approved transfer {p.FromWarehouse} to {p.ToWarehouse} failed to post",
                Reference = string.IsNullOrWhiteSpace(p.DraftNumber)
                    ? $"Held transfer {p.Id:D}"
                    : p.DraftNumber,
                Status = "Failed",
                SourceSystem = $"{p.LineCount} line(s), {p.TotalQuantity:0.####} qty - raised by {p.CreatedByName}",
                LastError = p.LastError,
                RetryCount = 0,
                MaxRetries = 0,
                CreatedAtUtc = p.CreatedAtUtc,
                OccurredAtUtc = p.DecidedAtUtc ?? p.CreatedAtUtc,
                NextRetryAtUtc = null,
                CanRetry = true
            })
            .ToList();
    }

    /// <summary>
    /// Approved changes to SAP transfer requests that failed to reach SAP. Guid keyed, and like
    /// the held transfers nothing reattempts them on its own.
    /// </summary>
    internal static async Task<List<ExceptionCenterItemDto>> LoadPendingRequestEditApplyFailuresAsync(
        ApplicationDbContext context,
        int perSourceLimit,
        CancellationToken cancellationToken)
    {
        var rows = await context.PendingTransferRequestEdits
            .AsNoTracking()
            .Where(e => e.Status == PendingTransferRequestEditStatuses.ApplyFailed)
            .OrderByDescending(e => e.DecidedAtUtc ?? e.CreatedAtUtc)
            .Take(perSourceLimit)
            .Select(e => new
            {
                e.Id,
                e.RequestDocEntry,
                e.RequestDocNum,
                e.FromWarehouse,
                e.ToWarehouse,
                e.CreatedByName,
                e.LastError,
                e.CreatedAtUtc,
                e.DecidedAtUtc
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(e => new ExceptionCenterItemDto
            {
                Source = ExceptionCenterSources.PendingTransferRequestEditApply,
                ItemId = 0,
                ItemKey = ExceptionCenterSources.Key(e.Id),
                Category = "SAP Posting",
                Title = $"Approved change to transfer request #{e.RequestDocNum} failed to apply",
                Reference = $"Transfer request #{e.RequestDocNum}",
                Status = "Failed",
                SourceSystem = $"DocEntry {e.RequestDocEntry}, {e.FromWarehouse} to {e.ToWarehouse} - proposed by {e.CreatedByName}",
                LastError = e.LastError,
                RetryCount = 0,
                MaxRetries = 0,
                CreatedAtUtc = e.CreatedAtUtc,
                OccurredAtUtc = e.DecidedAtUtc ?? e.CreatedAtUtc,
                NextRetryAtUtc = null,
                CanRetry = true
            })
            .ToList();
    }

    private static int GetStatusRank(string status)
        => status switch
        {
            "RequiresReview" => 3,
            "Failed" => 2,
            _ => 1
        };

    private static string BuildStateKey(string source, string itemKey) => $"{source}:{itemKey}";
}