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
    private const string FiscalDayLifecycleSource = ExceptionCenterSources.FiscalDayLifecycle;
    private const string FiscalReceiptIngestSource = ExceptionCenterSources.FiscalReceiptIngest;
    private const string VanSaleReceiptStorageSource = ExceptionCenterSources.VanSaleReceiptStorage;

    /// <summary>
    /// A fiscal day still unfinished this long after the handset opened it has stopped rather than slowed.
    /// </summary>
    /// <remarks>
    /// A flat figure although ZIMRA's own limit is per taxpayer and read from the device, because this is
    /// not the compliance deadline — the lifecycle warns against that one from the device's own
    /// <c>TaxPayerDayMaxHrs</c>. This is the far coarser question of whether a day is still moving, and a
    /// trading day plus a night is past any answer but no.
    /// </remarks>
    private const int FiscalDayStuckAfterHours = 30;

    /// <summary>
    /// Mirrors the signed-receipt drain's own cap. Past it the drain stops offering the receipt, so nothing
    /// reattempts it and the device it belongs to is stopped behind it.
    /// </summary>
    private const int MaxReceiptIngestAttempts = 8;

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

            // Both fiscal sources are measured in the taxpayer's clock: a fiscal day is opened, timed and
            // closed in local terms, and comparing it against a UTC instant moves the deadline by two hours.
            var fiscalDayStuckBeforeLocal = AuditService.ToCAT(now).AddHours(-FiscalDayStuckAfterHours);
            var fiscalDayItems = await LoadFiscalDayLifecycleFailuresAsync(
                context, fiscalDayStuckBeforeLocal, AnalysisScanLimit, cancellationToken);
            var fiscalReceiptItems = await LoadFiscalReceiptIngestFailuresAsync(
                context, AnalysisScanLimit, cancellationToken);

            var items = invoiceItems
                .Concat(transferItems)
                .Concat(mobileItems)
                .Concat(incomingPaymentItems)
                .Concat(paymentItems)
                .Concat(incidentItems)
                .Concat(pendingTransferItems)
                .Concat(pendingEditItems)
                .Concat(vanSaleItems)
                .Concat(fiscalDayItems)
                .Concat(fiscalReceiptItems)
                .ToList();

            EnsureItemKeys(items);
            await AttachOperatorStateAsync(items, cancellationToken);

            foreach (var item in items)
            {
                Enrich(item, now);
            }

            var exactTotals = await LoadExactTotalsAsync(
                stalledBefore, fiscalDayStuckBeforeLocal, cancellationToken);
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
            // A signed receipt this server failed to store has no document row to list it by — that is
            // the whole failure — so it lives on the incident table with the other two.
            .Where(i => i.Source == PaymentRejectedSource
                        || i.Source == CreditNoteFiscalizationSource
                        || i.Source == VanSaleReceiptStorageSource)
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
        DateTime fiscalDayStuckBeforeLocal,
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

            [VanSaleReceiptStorageSource] = await context.ExceptionCenterIncidents.CountAsync(
                i => i.Source == VanSaleReceiptStorageSource, cancellationToken),

            [PendingTransferPostSource] = await context.PendingInventoryTransfers.CountAsync(
                p => p.Status == PendingInventoryTransferStatuses.PostFailed, cancellationToken),

            [PendingEditApplySource] = await context.PendingTransferRequestEdits.CountAsync(
                e => e.Status == PendingTransferRequestEditStatuses.ApplyFailed, cancellationToken),

            [VanSalePostingSource] = await context.DesktopSales.CountAsync(
                VanSalePostingPredicate(
                    vanSalesPostingSettings.Value.WindowStart(VanSalesPostingSettings.CurrentTradingDate())),
                cancellationToken),

            [FiscalDayLifecycleSource] = await context.FiscalDayStates.CountAsync(
                FiscalDayLifecyclePredicate(fiscalDayStuckBeforeLocal), cancellationToken),

            [FiscalReceiptIngestSource] = await context.DesktopSales.CountAsync(
                FiscalReceiptIngestPredicate(), cancellationToken)
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
            VanSaleReceiptStorageSource => "Signed receipts this server failed to store",
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
    ///
    /// <para>
    /// <see cref="SaleSourceSystems.VanSalesOnline"/> is deliberately not in scope. Those rows carry a
    /// receipt for a sale SAP already invoiced in the request that made it — there is no posting job that
    /// owns them, so there is no posting failure they can be in, and nothing here would ever be the right
    /// thing to do about one. When an online van sale does go wrong it goes wrong at the receipt, and
    /// <see cref="FiscalReceiptIngestPredicate"/> — which asks about the receipt and not about the source
    /// — is what surfaces it, exactly as it does for an offline one.
    /// </para>
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

    /// <summary>
    /// Which fiscal days the exception center should be showing.
    /// </summary>
    /// <remarks>
    /// Two populations again, and the second is the one nothing else would ever mention. A day whose close
    /// or upload came back with an unknown outcome, or that FDMS refused outright, says so in its own status.
    /// A day that simply never moved says nothing: no error, no attempt, and every receipt in it still
    /// looking perfectly healthy — the customer has the printed receipt, SAP has the invoice, the platform
    /// has archived it. Only the day's own row knows ZIMRA was never told.
    ///
    /// A day merely waiting for this evening's close is normal operation and deliberately absent.
    /// </remarks>
    internal static System.Linq.Expressions.Expression<Func<FiscalDayStateEntity, bool>> FiscalDayLifecyclePredicate(
        DateTime stuckBeforeLocal)
        => day => day.Status == FiscalDayLifecycleStatus.NeedsReconciliation
                  || day.Status == FiscalDayLifecycleStatus.Failed
                  || (day.Status != FiscalDayLifecycleStatus.Submitted
                      && day.Status != FiscalDayLifecycleStatus.Closed
                      && day.OpenedAtLocal != null
                      && day.OpenedAtLocal < stuckBeforeLocal);

    /// <summary>
    /// Fiscal days that have stopped somewhere between a stamped receipt and ZIMRA holding it.
    /// </summary>
    /// <remarks>
    /// <c>CanRetry</c> is false on every row here, which is the point rather than an omission. A day whose
    /// outcome FDMS never confirmed is resolved by reading — the device's status, or the list of files FDMS
    /// accepted — and the lifecycle already does that on every pass. Closing a day twice or uploading one
    /// file twice is not idempotent at FDMS, so the button would offer the single action that cannot be
    /// taken back.
    /// </remarks>
    internal static async Task<List<ExceptionCenterItemDto>> LoadFiscalDayLifecycleFailuresAsync(
        ApplicationDbContext context,
        DateTime stuckBeforeLocal,
        int perSourceLimit,
        CancellationToken cancellationToken)
    {
        var rows = await context.FiscalDayStates
            .AsNoTracking()
            .Where(FiscalDayLifecyclePredicate(stuckBeforeLocal))
            // Oldest day first: the longer a day has been unreported, the closer the taxpayer is to a
            // filing that cannot be corrected.
            .OrderBy(day => day.OpenedAtLocal ?? day.CreatedAt)
            .ThenBy(day => day.Id)
            .Take(perSourceLimit)
            .Select(day => new
            {
                day.Id,
                day.DeviceId,
                day.FiscalDayNo,
                day.OpenedAtLocal,
                day.Status,
                day.IngestedReceiptCount,
                day.Attempts,
                day.LastError,
                day.CreatedAt,
                day.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(day => new ExceptionCenterItemDto
            {
                Source = FiscalDayLifecycleSource,
                ItemId = day.Id,
                Category = "Fiscalisation",
                Title = day.Status switch
                {
                    FiscalDayLifecycleStatus.NeedsReconciliation =>
                        "Fiscal day outcome unknown at ZIMRA",
                    FiscalDayLifecycleStatus.Failed =>
                        "Fiscal day refused on its way to ZIMRA",
                    _ => "Fiscal day has not reached ZIMRA"
                },
                Reference = $"Device {day.DeviceId}, fiscal day {day.FiscalDayNo}",
                Status = day.Status.ToString(),
                SourceSystem = day.OpenedAtLocal.HasValue
                    ? $"Opened {day.OpenedAtLocal:yyyy-MM-dd HH:mm}, {day.IngestedReceiptCount} receipt(s) archived"
                    : $"{day.IngestedReceiptCount} receipt(s) archived",
                Provider = "Fiscalisation",
                LastError = string.IsNullOrWhiteSpace(day.LastError)
                    // Nothing failed, which is exactly why this row is here and why a blank cell would read
                    // as "no problem".
                    ? "The day has not been closed, packaged or uploaded, so its receipts are not with ZIMRA."
                    : day.LastError,
                RetryCount = day.Attempts,
                MaxRetries = 0,
                CreatedAtUtc = day.CreatedAt,
                OccurredAtUtc = day.UpdatedAt,
                NextRetryAtUtc = null,
                CanRetry = false,
                LineCount = day.IngestedReceiptCount
            })
            .ToList();
    }

    /// <summary>
    /// Which signed receipts have stopped, as opposed to merely being in the queue.
    /// </summary>
    /// <remarks>
    /// A chain break and a missing signature can never be sent, whatever happens next. A receipt that has
    /// used up its attempts is no longer offered by the drain, so nothing reattempts it either. All three
    /// stop the whole device rather than the one receipt, because the platform accepts receipt N+1 only once
    /// it holds N.
    ///
    /// <para>
    /// <see cref="DesktopSaleReceiptIngestStatus.Unstamped"/> is the fourth and is not like the others, and
    /// it is here because leaving it out made it disappear. Before the online path was stamped at all, an
    /// unstamped van sale was written <see cref="DesktopSaleReceiptIngestStatus.Unsignable"/> and so showed
    /// up on this list; giving it a status of its own — correctly, because it took no receipt number and
    /// therefore blocks nothing — moved it off the Exception Center and onto the fiscalisation console
    /// alone. A van trading on a build that cannot stamp is exactly the thing an operator has to see, and
    /// the console is not where they look for work that needs doing.
    /// </para>
    ///
    /// <para>
    /// It is distinguished from a chain hole everywhere it is rendered: the title says the handset was
    /// never updated rather than that a receipt is stuck, and the text says nothing is blocked. The
    /// remedy is a handset update, not a reconciliation. Nothing else changes — the drain still skips
    /// these rows and the fiscal day still counts them as settled, both correct, because an unstamped
    /// sale is not in the device's chain at all.
    /// </para>
    /// </remarks>
    internal static System.Linq.Expressions.Expression<Func<DesktopSaleEntity, bool>> FiscalReceiptIngestPredicate()
        => sale => sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.ChainBroken
                   || sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unsignable
                   || sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unstamped
                   || ((sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Pending
                        || sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Failed)
                       && sale.ReceiptIngestAttempts >= MaxReceiptIngestAttempts);

    /// <summary>
    /// Signed van receipts the platform will never be given without someone intervening.
    /// </summary>
    /// <remarks>
    /// <c>CanRetry</c> is false because resending is what must not happen: the signature is chained onto its
    /// predecessor's hash, so a receipt the platform says does not fit cannot be made to fit by offering it
    /// again, and a receipt whose signature is missing cannot be produced at all.
    /// </remarks>
    internal static async Task<List<ExceptionCenterItemDto>> LoadFiscalReceiptIngestFailuresAsync(
        ApplicationDbContext context,
        int perSourceLimit,
        CancellationToken cancellationToken)
    {
        var rows = await context.DesktopSales
            .AsNoTracking()
            .Where(FiscalReceiptIngestPredicate())
            // Per device, in signing order: the earliest stuck receipt on a device is the one holding up
            // every receipt behind it, so it is the only one worth fixing first.
            .OrderBy(sale => sale.FiscalDeviceId)
            .ThenBy(sale => sale.ReceiptGlobalNo)
            .ThenBy(sale => sale.Id)
            .Take(perSourceLimit)
            .Select(sale => new
            {
                sale.Id,
                sale.ExternalReferenceId,
                sale.FiscalDeviceId,
                sale.FiscalDayNo,
                sale.ReceiptGlobalNo,
                sale.ReceiptIngestStatus,
                sale.ReceiptIngestAttempts,
                sale.ReceiptIngestError,
                sale.WarehouseCode,
                sale.CardCode,
                sale.RouteCustomerName,
                sale.TotalAmount,
                sale.Currency,
                sale.DocDate,
                sale.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(sale => new ExceptionCenterItemDto
            {
                Source = FiscalReceiptIngestSource,
                ItemId = sale.Id,
                Category = "Fiscalisation",
                Title = sale.ReceiptIngestStatus switch
                {
                    DesktopSaleReceiptIngestStatus.ChainBroken =>
                        "Signed receipt does not continue its device's chain",
                    DesktopSaleReceiptIngestStatus.Unsignable =>
                        "Van sale arrived without a usable signature",

                    // Named for the handset, not the receipt, because there is no receipt. The other
                    // titles on this source describe something stuck; this one must not, or it reads as a
                    // fourth kind of chain hole and gets worked as a reconciliation it cannot be.
                    DesktopSaleReceiptIngestStatus.Unstamped =>
                        "Van sold on a handset that cannot stamp receipts",

                    _ => "Signed receipt has used up its submission attempts"
                },
                Reference = string.IsNullOrWhiteSpace(sale.ExternalReferenceId)
                    ? $"Van sale #{sale.Id}"
                    : sale.ExternalReferenceId,
                Status = sale.ReceiptIngestStatus.ToString(),
                SourceSystem = $"Device {sale.FiscalDeviceId}, fiscal day {sale.FiscalDayNo}"
                               + (sale.ReceiptGlobalNo.HasValue ? $", receipt {sale.ReceiptGlobalNo}" : string.Empty),
                Provider = "Fiscalisation",
                LastError = sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unstamped
                    // Says what is and is not at stake, in that order, because the two are easy to swap.
                    // The sale is fine and the day is not held: this receipt took no number off the
                    // device's chain, so nothing is queued behind it. What is wrong is that a ZIMRA
                    // device is in the field making sales it never stamped, and the only thing that
                    // fixes that is the app on the handset.
                    ? "Nothing is blocked: this sale took no receipt number, so no other receipt is "
                      + "waiting behind it and the fiscal day can still close. The handset is on a build "
                      + "older than the signing release and is trading unstamped — update the app on this "
                      + "van, then turn on Fiscalisation:RequireStampedVanSales once the fleet is done."
                    : string.IsNullOrWhiteSpace(sale.ReceiptIngestError)
                        ? "The platform never took this receipt, so its fiscal day cannot be closed over it."
                        : sale.ReceiptIngestError,
                RetryCount = sale.ReceiptIngestAttempts,
                MaxRetries = 0,
                CreatedAtUtc = sale.CreatedAt,
                OccurredAtUtc = sale.CreatedAt,
                NextRetryAtUtc = null,
                CanRetry = false,
                Amount = sale.TotalAmount,
                Currency = sale.Currency,
                Counterparty = string.IsNullOrWhiteSpace(sale.RouteCustomerName) ? sale.CardCode : sale.RouteCustomerName,
                Location = sale.WarehouseCode
            })
            .ToList();
    }

    private static string BuildStateKey(string source, string itemKey) => $"{source}:{itemKey}";
}
