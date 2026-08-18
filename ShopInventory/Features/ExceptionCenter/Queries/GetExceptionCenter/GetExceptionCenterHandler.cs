using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;

/// <summary>
/// Assembles the exception center. Beyond listing failures it answers the three
/// questions the list on its own could not: what will never recover without a
/// human, why (grouped by root cause rather than by row), and what it is holding up
/// in money and volume.
/// </summary>
public sealed class GetExceptionCenterHandler(
    ApplicationDbContext context,
    IOptions<VanSalesPostingSettings> vanSalesPostingSettings,
    ILogger<GetExceptionCenterHandler> logger
) : IRequestHandler<GetExceptionCenterQuery, ErrorOr<ExceptionCenterDashboardDto>>
{
    private const int DefaultPageLimit = 150;

    /// <summary>
    /// How many rows per source the analysis reads. These tables hold exceptions, not
    /// traffic, so this is normally the whole population; when it is not, the dashboard
    /// says so rather than quietly describing a sample.
    /// </summary>
    private const int AnalysisScanLimit = 750;

    /// <summary>A row left in Processing for longer than this was abandoned by its worker.</summary>
    private static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(20);

    /// <summary>Past-due by more than this and the processor itself is suspect.</summary>
    private static readonly TimeSpan RetryOverdueAfter = TimeSpan.FromMinutes(30);

    private const string InvoiceQueueSource = ExceptionCenterSources.InvoiceQueue;
    private const string TransferQueueSource = ExceptionCenterSources.InventoryTransferQueue;
    private const string MobileQueueSource = ExceptionCenterSources.MobileOrderPostProcessing;
    private const string IncomingPaymentSource = ExceptionCenterSources.IncomingPaymentQueue;
    private const string PaymentSource = ExceptionCenterSources.PaymentCallback;
    private const string PaymentRejectedSource = ExceptionCenterSources.PaymentCallbackRejection;
    private const string CreditNoteFiscalizationSource = ExceptionCenterSources.CreditNoteFiscalization;
    private const string PendingTransferPostSource = ExceptionCenterSources.PendingInventoryTransferPost;
    private const string PendingEditApplySource = ExceptionCenterSources.PendingTransferRequestEditApply;
    private const string VanSalePostingSource = ExceptionCenterSources.VanSalePosting;

    private const string TriageBlocked = "Blocked";
    private const string TriageRetrying = "Retrying";
    private const string TriageStalled = "Stalled";

    public async Task<ErrorOr<ExceptionCenterDashboardDto>> Handle(
        GetExceptionCenterQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTime.UtcNow;
            var stalledBefore = now - StalledAfter;
            var pageLimit = Math.Clamp(request.Limit <= 0 ? DefaultPageLimit : request.Limit, 20, 500);

            var invoiceItems = await LoadInvoiceItemsAsync(stalledBefore, cancellationToken);
            var transferItems = await LoadTransferItemsAsync(stalledBefore, cancellationToken);
            var mobileItems = await LoadMobileItemsAsync(stalledBefore, cancellationToken);
            var incomingPaymentItems = await LoadIncomingPaymentItemsAsync(stalledBefore, cancellationToken);
            var paymentItems = await LoadPaymentCallbackItemsAsync(cancellationToken);
            var incidentItems = await LoadIncidentItemsAsync(cancellationToken);

            var pendingTransferItems = await LoadPendingTransferPostFailuresAsync(context, AnalysisScanLimit, cancellationToken);
            var pendingEditItems = await LoadPendingRequestEditApplyFailuresAsync(context, AnalysisScanLimit, cancellationToken);

            var vanSaleWindowStart = vanSalesPostingSettings.Value.WindowStart(
                VanSalesPostingSettings.CurrentTradingDate());
            var vanSaleItems = await LoadVanSalePostingFailuresAsync(
                context, vanSaleWindowStart, AnalysisScanLimit, cancellationToken);

            var items = invoiceItems
                .Concat(transferItems)
                .Concat(mobileItems)
                .Concat(incomingPaymentItems)
                .Concat(paymentItems)
                .Concat(incidentItems)
                .Concat(pendingTransferItems)
                .Concat(pendingEditItems)
                .Concat(vanSaleItems)
                .ToList();

            EnsureItemKeys(items);
            await AttachOperatorStateAsync(items, cancellationToken);

            foreach (var item in items)
            {
                Enrich(item, now);
            }

            var exactTotals = await LoadExactTotalsAsync(stalledBefore, cancellationToken);
            var scannedBySource = items
                .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            var analysisTruncated = exactTotals.Any(total =>
                total.Value > scannedBySource.GetValueOrDefault(total.Key));

            var ordered = items
                .OrderByDescending(item => TriageRank(item.Triage))
                .ThenBy(item => item.IsAcknowledged)
                .ThenBy(item => !string.IsNullOrWhiteSpace(item.AssignedToUsername))
                .ThenBy(item => item.OccurredAtUtc ?? item.CreatedAtUtc)
                .ToList();

            return new ExceptionCenterDashboardDto
            {
                GeneratedAtUtc = now,
                Triage = BuildTriage(items, request.Assignee, now),
                Clusters = BuildClusters(items),
                Sources = BuildSources(items, exactTotals),
                Exposure = BuildExposure(items),
                Trend = BuildTrend(items, now),
                Items = ordered.Take(pageLimit).ToList(),
                TotalItemCount = exactTotals.Values.Sum(),
                ItemsTruncated = ordered.Count > pageLimit || analysisTruncated,
                AnalysisTruncated = analysisTruncated
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load exception center dashboard");
            return Errors.ExceptionCenter.LoadFailed("Failed to load exception center dashboard.");
        }
    }

    // ── Source loaders ──────────────────────────────────────────────────────
    //
    // Each pulls the failing rows plus the ones abandoned mid-flight, and carries
    // through the business context (who, where, how much) that makes a row
    // triageable without opening SAP.

    private Task<List<ExceptionCenterItemDto>> LoadInvoiceItemsAsync(
        DateTime stalledBefore,
        CancellationToken cancellationToken)
        => context.InvoiceQueue
            .AsNoTracking()
            .Where(q => q.Status == InvoiceQueueStatus.Failed
                        || q.Status == InvoiceQueueStatus.RequiresReview
                        || (q.Status == InvoiceQueueStatus.Processing
                            && q.ProcessingStartedAt != null
                            && q.ProcessingStartedAt < stalledBefore))
            .OrderByDescending(q => q.ProcessedAt ?? q.ProcessingStartedAt ?? q.CreatedAt)
            .Take(AnalysisScanLimit)
            .Select(q => new ExceptionCenterItemDto
            {
                Source = InvoiceQueueSource,
                ItemId = q.Id,
                Category = q.LastError != null && EF.Functions.ILike(q.LastError, "%fiscalization%")
                    ? "Fiscalisation"
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
                ProcessingStartedAtUtc = q.ProcessingStartedAt,
                CanRetry = q.Status == InvoiceQueueStatus.Failed || q.Status == InvoiceQueueStatus.RequiresReview,
                Amount = q.TotalAmount,
                Currency = q.Currency,
                Counterparty = q.CustomerCode,
                Location = q.WarehouseCode,
                CreatedBy = q.CreatedBy
            })
            .ToListAsync(cancellationToken);

    private Task<List<ExceptionCenterItemDto>> LoadTransferItemsAsync(
        DateTime stalledBefore,
        CancellationToken cancellationToken)
        => context.InventoryTransferQueue
            .AsNoTracking()
            .Where(q => q.Status == InventoryTransferQueueStatus.Failed
                        || q.Status == InventoryTransferQueueStatus.RequiresReview
                        || (q.Status == InventoryTransferQueueStatus.Processing
                            && q.ProcessingStartedAt != null
                            && q.ProcessingStartedAt < stalledBefore))
            .OrderByDescending(q => q.ProcessedAt ?? q.ProcessingStartedAt ?? q.CreatedAt)
            .Take(AnalysisScanLimit)
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
                ProcessingStartedAtUtc = q.ProcessingStartedAt,
                CanRetry = q.Status == InventoryTransferQueueStatus.Failed || q.Status == InventoryTransferQueueStatus.RequiresReview,
                Location = q.FromWarehouse + " → " + q.ToWarehouse,
                LineCount = q.LineCount,
                CreatedBy = q.CreatedBy
            })
            .ToListAsync(cancellationToken);

    private Task<List<ExceptionCenterItemDto>> LoadMobileItemsAsync(
        DateTime stalledBefore,
        CancellationToken cancellationToken)
        => context.MobileOrderPostProcessingQueue
            .AsNoTracking()
            .Where(q => q.Status == MobileOrderPostProcessingQueueStatus.Failed
                        || q.Status == MobileOrderPostProcessingQueueStatus.RequiresReview
                        || (q.Status == MobileOrderPostProcessingQueueStatus.Processing
                            && q.ProcessingStartedAt != null
                            && q.ProcessingStartedAt < stalledBefore))
            .OrderByDescending(q => q.ProcessedAt ?? q.ProcessingStartedAt ?? q.CreatedAt)
            .Take(AnalysisScanLimit)
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
                ProcessingStartedAtUtc = q.ProcessingStartedAt,
                CanRetry = q.Status == MobileOrderPostProcessingQueueStatus.Failed || q.Status == MobileOrderPostProcessingQueueStatus.RequiresReview,
                LineCount = q.LineCount
            })
            .ToListAsync(cancellationToken);

    private Task<List<ExceptionCenterItemDto>> LoadIncomingPaymentItemsAsync(
        DateTime stalledBefore,
        CancellationToken cancellationToken)
        => context.IncomingPaymentQueue
            .AsNoTracking()
            .Where(q => q.Status == IncomingPaymentQueueStatus.Failed
                        || q.Status == IncomingPaymentQueueStatus.RequiresReview
                        || (q.Status == IncomingPaymentQueueStatus.Processing
                            && q.ProcessingStartedAt != null
                            && q.ProcessingStartedAt < stalledBefore))
            .OrderByDescending(q => q.ProcessedAt ?? q.ProcessingStartedAt ?? q.CreatedAt)
            .Take(AnalysisScanLimit)
            .Select(q => new ExceptionCenterItemDto
            {
                Source = IncomingPaymentSource,
                ItemId = q.Id,
                Category = "SAP Posting",
                Title = "Customer receipt posting issue",
                Reference = q.ExternalReference,
                Status = q.Status.ToString(),
                SourceSystem = q.SourceSystem,
                LastError = q.LastError,
                RetryCount = q.RetryCount,
                MaxRetries = q.MaxRetries,
                CreatedAtUtc = q.CreatedAt,
                OccurredAtUtc = q.ProcessedAt ?? q.ProcessingStartedAt ?? q.CreatedAt,
                NextRetryAtUtc = q.NextRetryAt,
                ProcessingStartedAtUtc = q.ProcessingStartedAt,
                CanRetry = q.Status == IncomingPaymentQueueStatus.Failed || q.Status == IncomingPaymentQueueStatus.RequiresReview,
                Amount = q.TotalAmount,
                Currency = q.Currency,
                Counterparty = q.CustomerCode,
                CreatedBy = q.CreatedBy
            })
            .ToListAsync(cancellationToken);

    private Task<List<ExceptionCenterItemDto>> LoadPaymentCallbackItemsAsync(CancellationToken cancellationToken)
        => context.PaymentTransactions
            .AsNoTracking()
            .Where(t => t.Status == PaymentStatus.Failed)
            .OrderByDescending(t => t.UpdatedAt ?? t.CompletedAt ?? t.CreatedAt)
            .Take(AnalysisScanLimit)
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
                CanRetry = false,
                Amount = t.Amount,
                Currency = t.Currency,
                Counterparty = t.CustomerCode
            })
            .ToListAsync(cancellationToken);

    private Task<List<ExceptionCenterItemDto>> LoadIncidentItemsAsync(CancellationToken cancellationToken)
        => context.ExceptionCenterIncidents
            .AsNoTracking()
            .Where(i => i.Source == PaymentRejectedSource || i.Source == CreditNoteFiscalizationSource)
            .OrderByDescending(i => i.OccurredAtUtc ?? i.CreatedAtUtc)
            .Take(AnalysisScanLimit)
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

    /// <summary>
    /// Exact population size per source, so the headline numbers stay honest even
    /// when a source holds more rows than the analysis scan reads.
    /// </summary>
    private async Task<Dictionary<string, int>> LoadExactTotalsAsync(
        DateTime stalledBefore,
        CancellationToken cancellationToken)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [InvoiceQueueSource] = await context.InvoiceQueue.CountAsync(
                q => q.Status == InvoiceQueueStatus.Failed
                     || q.Status == InvoiceQueueStatus.RequiresReview
                     || (q.Status == InvoiceQueueStatus.Processing
                         && q.ProcessingStartedAt != null
                         && q.ProcessingStartedAt < stalledBefore),
                cancellationToken),

            [TransferQueueSource] = await context.InventoryTransferQueue.CountAsync(
                q => q.Status == InventoryTransferQueueStatus.Failed
                     || q.Status == InventoryTransferQueueStatus.RequiresReview
                     || (q.Status == InventoryTransferQueueStatus.Processing
                         && q.ProcessingStartedAt != null
                         && q.ProcessingStartedAt < stalledBefore),
                cancellationToken),

            [MobileQueueSource] = await context.MobileOrderPostProcessingQueue.CountAsync(
                q => q.Status == MobileOrderPostProcessingQueueStatus.Failed
                     || q.Status == MobileOrderPostProcessingQueueStatus.RequiresReview
                     || (q.Status == MobileOrderPostProcessingQueueStatus.Processing
                         && q.ProcessingStartedAt != null
                         && q.ProcessingStartedAt < stalledBefore),
                cancellationToken),

            [IncomingPaymentSource] = await context.IncomingPaymentQueue.CountAsync(
                q => q.Status == IncomingPaymentQueueStatus.Failed
                     || q.Status == IncomingPaymentQueueStatus.RequiresReview
                     || (q.Status == IncomingPaymentQueueStatus.Processing
                         && q.ProcessingStartedAt != null
                         && q.ProcessingStartedAt < stalledBefore),
                cancellationToken),

            [PaymentSource] = await context.PaymentTransactions.CountAsync(
                t => t.Status == PaymentStatus.Failed, cancellationToken),

            [PaymentRejectedSource] = await context.ExceptionCenterIncidents.CountAsync(
                i => i.Source == PaymentRejectedSource, cancellationToken),

            [CreditNoteFiscalizationSource] = await context.ExceptionCenterIncidents.CountAsync(
                i => i.Source == CreditNoteFiscalizationSource, cancellationToken),

            [PendingTransferPostSource] = await context.PendingInventoryTransfers.CountAsync(
                p => p.Status == PendingInventoryTransferStatuses.PostFailed, cancellationToken),

            [PendingEditApplySource] = await context.PendingTransferRequestEdits.CountAsync(
                e => e.Status == PendingTransferRequestEditStatuses.ApplyFailed, cancellationToken),

            [VanSalePostingSource] = await context.DesktopSales.CountAsync(
                VanSalePostingPredicate(
                    vanSalesPostingSettings.Value.WindowStart(VanSalesPostingSettings.CurrentTradingDate())),
                cancellationToken)
        };

    private async Task AttachOperatorStateAsync(
        List<ExceptionCenterItemDto> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        // Matched on the routing key rather than the int id, so Guid-keyed sources —
        // which hold zero in ItemId — do not all collide on one another's state.
        var sources = items.Select(item => item.Source).Distinct().ToList();
        var itemKeys = items.Select(item => item.ItemKey).Distinct().ToList();

        var states = await context.ExceptionCenterItemStates
            .AsNoTracking()
            .Where(state => sources.Contains(state.Source)
                            && state.ItemKey != null
                            && itemKeys.Contains(state.ItemKey))
            .ToListAsync(cancellationToken);

        ApplyStates(items, states);
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
    /// Adds the two things a raw row cannot say for itself: which root cause it
    /// belongs to, and whether it is going to recover on its own.
    /// </summary>
    internal static void Enrich(ExceptionCenterItemDto item, DateTime now)
    {
        var classification = ExceptionCenterErrorClassifier.Classify(item.LastError, item.Category);
        item.ClusterSignature = classification.Signature;
        item.RootCause = classification.Label;
        item.Family = classification.Family;

        item.IsRetryOverdue = item.NextRetryAtUtc.HasValue
                              && item.NextRetryAtUtc.Value < now - RetryOverdueAfter;

        var isProcessing = string.Equals(item.Status, "Processing", StringComparison.OrdinalIgnoreCase);
        var requiresReview = string.Equals(item.Status, "RequiresReview", StringComparison.OrdinalIgnoreCase);
        var attemptsExhausted = item.MaxRetries > 0 && item.RetryCount >= item.MaxRetries;

        // No attempt budget means nothing reattempts this on a timer, so it is a human's
        // problem however it got here. CanRetry is not the test — it says a human may press
        // retry, which is true of a held transfer precisely because nothing else will.
        var hasNoAutomaticRecovery = item.MaxRetries == 0;

        item.Triage = isProcessing
            ? TriageStalled
            : requiresReview || attemptsExhausted || hasNoAutomaticRecovery
                ? TriageBlocked
                : TriageRetrying;
    }

    // ── Facets ──────────────────────────────────────────────────────────────

    private static ExceptionCenterTriageDto BuildTriage(
        List<ExceptionCenterItemDto> items,
        string? assignee,
        DateTime now)
    {
        var blocked = items.Where(item => item.Triage == TriageBlocked).ToList();
        var occurrences = items
            .Select(item => item.OccurredAtUtc ?? item.CreatedAtUtc)
            .ToList();

        var upcomingRetries = items
            .Where(item => item.NextRetryAtUtc.HasValue && item.NextRetryAtUtc.Value >= now)
            .Select(item => item.NextRetryAtUtc!.Value)
            .ToList();

        return new ExceptionCenterTriageDto
        {
            BlockedCount = blocked.Count,
            BlockedUnassignedCount = blocked.Count(item => string.IsNullOrWhiteSpace(item.AssignedToUsername)),
            BlockedUnacknowledgedCount = blocked.Count(item => !item.IsAcknowledged),
            RetryingCount = items.Count(item => item.Triage == TriageRetrying),
            StalledCount = items.Count(item => item.Triage == TriageStalled),
            RetryOverdueCount = items.Count(item => item.IsRetryOverdue),
            AcknowledgedCount = items.Count(item => item.IsAcknowledged),
            AssignedToMeCount = string.IsNullOrWhiteSpace(assignee)
                ? 0
                : items.Count(item => string.Equals(item.AssignedToUsername, assignee, StringComparison.OrdinalIgnoreCase)),

            NewLastHourCount = occurrences.Count(occurred => occurred >= now.AddHours(-1)),
            NewLast24hCount = occurrences.Count(occurred => occurred >= now.AddHours(-24)),
            PreviousDayCount = occurrences.Count(occurred => occurred >= now.AddHours(-48) && occurred < now.AddHours(-24)),

            OldestOpenAtUtc = occurrences.Count == 0 ? null : occurrences.Min(),
            NewestOpenAtUtc = occurrences.Count == 0 ? null : occurrences.Max(),
            NextRetryAtUtc = upcomingRetries.Count == 0 ? null : upcomingRetries.Min(),

            AgeUnder1hCount = occurrences.Count(occurred => occurred >= now.AddHours(-1)),
            Age1To24hCount = occurrences.Count(occurred => occurred < now.AddHours(-1) && occurred >= now.AddHours(-24)),
            Age1To7dCount = occurrences.Count(occurred => occurred < now.AddHours(-24) && occurred >= now.AddDays(-7)),
            AgeOver7dCount = occurrences.Count(occurred => occurred < now.AddDays(-7))
        };
    }

    private static List<ExceptionCenterClusterDto> BuildClusters(List<ExceptionCenterItemDto> items)
        => items
            .GroupBy(item => item.ClusterSignature ?? "unknown", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var members = group.ToList();
                var representative = members[0];
                var classification = ExceptionCenterErrorClassifier.Classify(
                    representative.LastError,
                    representative.Category);

                return new ExceptionCenterClusterDto
                {
                    Signature = group.Key,
                    Label = classification.Label,
                    Guidance = classification.Guidance,
                    Family = classification.Family,
                    SampleError = members
                        .Select(item => item.LastError)
                        .FirstOrDefault(error => !string.IsNullOrWhiteSpace(error)) ?? string.Empty,
                    Count = members.Count,
                    BlockedCount = members.Count(item => item.Triage == TriageBlocked),
                    RetryingCount = members.Count(item => item.Triage == TriageRetrying),
                    RetryableCount = members.Count(item => item.CanRetry),
                    Sources = members
                        .Select(item => item.Source)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    Categories = members
                        .Select(item => item.Category)
                        .Where(category => !string.IsNullOrWhiteSpace(category))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    SampleReferences = members
                        .Select(item => item.Reference)
                        .Where(reference => !string.IsNullOrWhiteSpace(reference))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(6)
                        .ToList(),
                    FirstSeenUtc = members.Min(item => item.OccurredAtUtc ?? item.CreatedAtUtc),
                    LastSeenUtc = members.Max(item => item.OccurredAtUtc ?? item.CreatedAtUtc),
                    Exposure = BuildMoneyExposure(members, "Held up"),
                    RetryableItems = members
                        .Where(item => item.CanRetry)
                        .Select(item => new ExceptionCenterItemRefDto { Source = item.Source, ItemKey = item.ItemKey })
                        .ToList()
                };
            })
            // Blocked work first, then sheer volume: the cause holding the most
            // unrecoverable documents is the one worth looking at first.
            .OrderByDescending(cluster => cluster.BlockedCount)
            .ThenByDescending(cluster => cluster.Count)
            .ThenByDescending(cluster => cluster.LastSeenUtc)
            .ToList();

    private static List<ExceptionCenterSourceDto> BuildSources(
        List<ExceptionCenterItemDto> items,
        Dictionary<string, int> exactTotals)
    {
        var scanned = items
            .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        return exactTotals
            .Where(total => total.Value > 0)
            .Select(total =>
            {
                List<ExceptionCenterItemDto> members = scanned.GetValueOrDefault(total.Key) ?? [];

                return new ExceptionCenterSourceDto
                {
                    Source = total.Key,
                    DisplayName = DescribeSource(total.Key),
                    Category = members.FirstOrDefault()?.Category ?? string.Empty,
                    TotalCount = total.Value,
                    BlockedCount = members.Count(item => item.Triage == TriageBlocked),
                    RetryingCount = members.Count(item => item.Triage == TriageRetrying),
                    StalledCount = members.Count(item => item.Triage == TriageStalled),
                    OldestOpenAtUtc = members.Count == 0
                        ? null
                        : members.Min(item => item.OccurredAtUtc ?? item.CreatedAtUtc),
                    LastFailureAtUtc = members.Count == 0
                        ? null
                        : members.Max(item => item.OccurredAtUtc ?? item.CreatedAtUtc),
                    RetrySupported = members.Any(item => item.CanRetry)
                };
            })
            .OrderByDescending(source => source.BlockedCount)
            .ThenByDescending(source => source.TotalCount)
            .ToList();
    }

    private static List<ExceptionCenterExposureDto> BuildExposure(List<ExceptionCenterItemDto> items)
    {
        var exposure = new List<ExceptionCenterExposureDto>();

        exposure.AddRange(BuildMoneyExposure(
            items.Where(item => item.Source == InvoiceQueueSource).ToList(),
            "Invoices not posted"));

        exposure.AddRange(BuildMoneyExposure(
            items.Where(item => item.Source == IncomingPaymentSource).ToList(),
            "Receipts not posted"));

        exposure.AddRange(BuildMoneyExposure(
            items.Where(item => item.Source == PaymentSource).ToList(),
            "Gateway payments failed"));

        var transferLines = items
            .Where(item => item.Source == TransferQueueSource && item.LineCount.HasValue)
            .ToList();

        if (transferLines.Count > 0)
        {
            exposure.Add(new ExceptionCenterExposureDto
            {
                Label = "Transfer lines held",
                Unit = "lines",
                Amount = transferLines.Sum(item => item.LineCount ?? 0),
                ItemCount = transferLines.Count
            });
        }

        return exposure
            .OrderByDescending(entry => entry.Currency != null)
            .ThenByDescending(entry => entry.Amount)
            .ToList();
    }

    private static List<ExceptionCenterExposureDto> BuildMoneyExposure(
        List<ExceptionCenterItemDto> items,
        string label)
        => items
            .Where(item => item.Amount.HasValue && item.Amount.Value != 0)
            .GroupBy(item => item.Currency ?? "USD", StringComparer.OrdinalIgnoreCase)
            .Select(group => new ExceptionCenterExposureDto
            {
                Label = label,
                Currency = group.Key.ToUpperInvariant(),
                Amount = group.Sum(item => item.Amount ?? 0m),
                ItemCount = group.Count()
            })
            .OrderByDescending(entry => entry.Amount)
            .ToList();

    /// <summary>
    /// Arrivals per hour over the last day. A flat line means an old backlog; a spike
    /// on the right means something is breaking right now.
    /// </summary>
    private static List<ExceptionCenterTrendPointDto> BuildTrend(
        List<ExceptionCenterItemDto> items,
        DateTime now)
    {
        var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);

        var counts = items
            .Select(item => item.OccurredAtUtc ?? item.CreatedAtUtc)
            .Where(occurred => occurred >= currentHour.AddHours(-23))
            .GroupBy(occurred => new DateTime(occurred.Year, occurred.Month, occurred.Day, occurred.Hour, 0, 0, DateTimeKind.Utc))
            .ToDictionary(group => group.Key, group => group.Count());

        return Enumerable.Range(0, 24)
            .Select(offset =>
            {
                var hour = currentHour.AddHours(offset - 23);
                return new ExceptionCenterTrendPointDto
                {
                    HourUtc = hour,
                    Count = counts.GetValueOrDefault(hour)
                };
            })
            .ToList();
    }

    private static string DescribeSource(string source)
        => source switch
        {
            InvoiceQueueSource => "Invoice posting queue",
            TransferQueueSource => "Inventory transfer queue",
            MobileQueueSource => "Mobile order post-processing",
            IncomingPaymentSource => "Customer receipt queue",
            PaymentSource => "Payment gateway callbacks",
            PaymentRejectedSource => "Rejected payment callbacks",
            CreditNoteFiscalizationSource => "Credit note fiscalization",
            PendingTransferPostSource => "Approved transfers awaiting SAP",
            PendingEditApplySource => "Approved request changes awaiting SAP",
            VanSalePostingSource => "Van sales awaiting SAP",
            _ => source
        };

    private static int TriageRank(string triage)
        => triage switch
        {
            TriageBlocked => 3,
            TriageStalled => 2,
            _ => 1
        };

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

    /// <summary>
    /// What counts as a van sale the exception center should be showing. Written once and shared by the
    /// listing and the count, so the headline number cannot disagree with the rows under it.
    /// </summary>
    /// <remarks>
    /// Two populations, and the second is the reason this source exists. A sale SAP refused says so in
    /// <c>LastPostingError</c> and is offered again by the next pass. A sale that fell out of the posting
    /// window says nothing at all: no error, no attempts, and no run will ever ask for its trading day
    /// again, because the window only moves forward. Before this source, that second kind was invisible.
    ///
    /// A van sale merely waiting for the next pass is neither, and is deliberately not here — the whole
    /// route works by holding sales for a while, so listing them would report normal operation as a fault.
    /// </remarks>
    internal static System.Linq.Expressions.Expression<Func<DesktopSaleEntity, bool>> VanSalePostingPredicate(
        DateTime windowStart)
        => sale => sale.SourceSystem == SaleSourceSystems.VanSales
                   && sale.ConsolidationStatus == DesktopSaleConsolidationStatus.Pending
                   && (sale.LastPostingError != null || sale.DocDate < windowStart);

    /// <summary>
    /// Fiscalised van sales that have not reached SAP. Int keyed, on the sale's own id.
    /// </summary>
    /// <remarks>
    /// <c>MaxRetries</c> carries the distinction the triage reads: the posting job's attempt cap for a
    /// sale still inside the window, and zero for one that has fallen outside it. Zero is what
    /// <see cref="Enrich"/> treats as "nothing reattempts this on a timer", which for a stranded sale is
    /// the literal truth and puts it in front of a human instead of leaving it labelled as retrying.
    /// </remarks>
    internal static async Task<List<ExceptionCenterItemDto>> LoadVanSalePostingFailuresAsync(
        ApplicationDbContext context,
        DateTime windowStart,
        int perSourceLimit,
        CancellationToken cancellationToken)
    {
        var rows = await context.DesktopSales
            .AsNoTracking()
            .Where(VanSalePostingPredicate(windowStart))
            // Oldest trading day first: the further back a sale is, the longer its takings have been
            // missing from SAP and the less likely anything else is going to mention it.
            .OrderBy(s => s.DocDate)
            .ThenBy(s => s.Id)
            .Take(perSourceLimit)
            .Select(s => new
            {
                s.Id,
                s.ExternalReferenceId,
                s.DocDate,
                s.CardCode,
                s.RouteCustomerName,
                s.TotalAmount,
                s.Currency,
                s.WarehouseCode,
                s.ReceiptGlobalNo,
                s.PostingAttempts,
                s.LastPostingError,
                s.CreatedAt,
                LineCount = s.Lines.Count
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(s =>
            {
                var stranded = s.DocDate.Date < windowStart.Date;

                return new ExceptionCenterItemDto
                {
                    Source = VanSalePostingSource,
                    ItemId = s.Id,
                    Category = "SAP Posting",
                    Title = stranded
                        ? "Van sale stranded outside the posting window"
                        : "Van sale posting issue",
                    Reference = string.IsNullOrWhiteSpace(s.ExternalReferenceId)
                        ? $"Van sale #{s.Id}"
                        : s.ExternalReferenceId,
                    Status = stranded ? "Stranded" : "Failed",
                    SourceSystem = $"Van {s.WarehouseCode}, sold {s.DocDate:yyyy-MM-dd}"
                                   + (s.ReceiptGlobalNo.HasValue ? $", ZIMRA receipt {s.ReceiptGlobalNo}" : string.Empty),
                    LastError = stranded && string.IsNullOrWhiteSpace(s.LastPostingError)
                        // It has no error of its own to report, and a blank cell would read as "no
                        // problem here" on the one row where nothing at all has happened.
                        ? $"Sold on {s.DocDate:yyyy-MM-dd}, which is outside the posting window; no run will offer it to SAP again."
                        : s.LastPostingError,
                    RetryCount = s.PostingAttempts,
                    MaxRetries = stranded ? 0 : VanSalesEndOfDayPostingService.MaxPostingAttempts,
                    CreatedAtUtc = s.CreatedAt,
                    OccurredAtUtc = s.CreatedAt,
                    NextRetryAtUtc = null,
                    CanRetry = true,
                    Amount = s.TotalAmount,
                    Currency = s.Currency,
                    Counterparty = string.IsNullOrWhiteSpace(s.RouteCustomerName) ? s.CardCode : s.RouteCustomerName,
                    Location = s.WarehouseCode,
                    LineCount = s.LineCount
                };
            })
            .ToList();
    }

    private static string BuildStateKey(string source, string itemKey) => $"{source}:{itemKey}";
}
