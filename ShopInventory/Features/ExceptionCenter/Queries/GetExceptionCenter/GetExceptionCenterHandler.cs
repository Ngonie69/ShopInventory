using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;

/// <summary>
/// Assembles the exception center. Beyond listing failures it answers the three
/// questions the list on its own could not: what will never recover without a
/// human, why (grouped by root cause rather than by row), and what it is holding up
/// in money and volume.
/// </summary>
public sealed class GetExceptionCenterHandler(
    ApplicationDbContext context,
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

    private const string InvoiceQueueSource = "invoice-queue";
    private const string TransferQueueSource = "inventory-transfer-queue";
    private const string MobileQueueSource = "mobile-order-post-processing";
    private const string IncomingPaymentSource = "incoming-payment-queue";
    private const string PaymentSource = "payment-callback";
    private const string PaymentRejectedSource = "payment-callback-rejection";
    private const string CreditNoteFiscalizationSource = "credit-note-fiscalization";

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

            var items = invoiceItems
                .Concat(transferItems)
                .Concat(mobileItems)
                .Concat(incomingPaymentItems)
                .Concat(paymentItems)
                .Concat(incidentItems)
                .ToList();

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
                i => i.Source == CreditNoteFiscalizationSource, cancellationToken)
        };

    private async Task AttachOperatorStateAsync(
        List<ExceptionCenterItemDto> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        var sources = items.Select(item => item.Source).Distinct().ToList();
        var itemIds = items.Select(item => item.ItemId).Distinct().ToList();

        var states = await context.ExceptionCenterItemStates
            .AsNoTracking()
            .Where(state => sources.Contains(state.Source) && itemIds.Contains(state.ItemId))
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
    }

    /// <summary>
    /// Adds the two things a raw row cannot say for itself: which root cause it
    /// belongs to, and whether it is going to recover on its own.
    /// </summary>
    private static void Enrich(ExceptionCenterItemDto item, DateTime now)
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

        // A payment callback has no retry mechanism at all, so it is always a human's problem.
        var hasNoAutomaticRecovery = item.MaxRetries == 0 && !item.CanRetry;

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
                        .Select(item => new ExceptionCenterItemRefDto { Source = item.Source, ItemId = item.ItemId })
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
            _ => source
        };

    private static int TriageRank(string triage)
        => triage switch
        {
            TriageBlocked => 3,
            TriageStalled => 2,
            _ => 1
        };

    private static string BuildStateKey(string source, int itemId) => $"{source}:{itemId}";
}
