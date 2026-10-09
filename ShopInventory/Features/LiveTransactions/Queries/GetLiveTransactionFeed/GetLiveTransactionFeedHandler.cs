using System.Globalization;
using System.Net;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.LiveTransactions.Queries.GetLiveTransactionFeed;

/// <summary>
/// Merges this system's transactions with the Fiscalisation platform's activity into one timeline, and
/// pairs each fiscal receipt with the sale or invoice it fiscalised.
/// </summary>
/// <remarks>
/// Each source is read oldest-first and capped separately, then merged. When a source is capped, the
/// merged page stops at that source's last event: a later event from another source would otherwise
/// advance the reader's cursor past rows the capped source never returned.
/// </remarks>
public sealed class GetLiveTransactionFeedHandler(
    ApplicationDbContext context,
    IFiscalActivityFeedClient fiscalActivityFeed,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    TimeProvider timeProvider,
    ILogger<GetLiveTransactionFeedHandler> logger
) : IRequestHandler<GetLiveTransactionFeedQuery, ErrorOr<LiveTransactionFeedDto>>
{
    internal const int DetailMaxLength = 300;

    public async Task<ErrorOr<LiveTransactionFeedDto>> Handle(
        GetLiveTransactionFeedQuery request,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var sinceUtc = request.SinceUtc is { } since
            ? GetLiveTransactionFeedValidator.ToUtc(since)
            : AuditService.FromCAT(AuditService.ToCAT(nowUtc).Date);
        var limit = request.Limit;

        var page = new PageBuilder(limit);

        page.Add(await ReadSalesAsync(sinceUtc, limit, cancellationToken));
        page.Add(await ReadInvoicesAsync(sinceUtc, limit, cancellationToken));
        page.Add(await ReadIncomingPaymentsAsync(sinceUtc, limit, cancellationToken));
        page.Add(await ReadMobilePaymentsAsync(sinceUtc, limit, cancellationToken));

        var (fiscalEvents, fiscalHasMore, fiscalMessage) = await ReadFiscalAsync(sinceUtc, limit, cancellationToken);
        if (fiscalEvents is not null)
        {
            await LinkFiscalEventsAsync(fiscalEvents, cancellationToken);
            page.Add(fiscalEvents, alreadyCapped: fiscalHasMore);
        }

        var (events, hasMore) = page.Build();

        return new LiveTransactionFeedDto
        {
            ServerTimeUtc = nowUtc,
            HasMore = hasMore,
            FiscalFeedAvailable = fiscalEvents is not null,
            FiscalFeedMessage = fiscalMessage,
            Events = events
        };
    }

    private async Task<List<LiveTransactionEventDto>> ReadSalesAsync(DateTime sinceUtc, int limit, CancellationToken ct)
    {
        var rows = await context.DesktopSales
            .AsNoTracking()
            .Where(s => s.CreatedAt >= sinceUtc)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .Take(limit + 1)
            .Select(s => new
            {
                s.Id,
                s.ExternalReferenceId,
                s.SourceSystem,
                s.CardCode,
                s.CardName,
                s.TotalAmount,
                s.Currency,
                s.WarehouseCode,
                s.FiscalizationStatus,
                s.FiscalError,
                s.SapDocNum,
                s.LastPostingError,
                s.CreatedAt
            })
            .ToListAsync(ct);

        return rows.Select(s => new LiveTransactionEventDto
        {
            EventId = SaleEventId(s.Id),
            Kind = LiveTransactionKinds.Sale,
            OccurredAtUtc = AsUtc(s.CreatedAt),
            Reference = s.ExternalReferenceId,
            Counterparty = s.CardName ?? s.CardCode,
            Amount = s.TotalAmount,
            Currency = s.Currency,
            Location = s.WarehouseCode,
            Channel = s.SourceSystem,
            Status = s.SapDocNum is null ? "Awaiting SAP" : "Posted to SAP",
            FiscalStatus = s.FiscalizationStatus.ToString(),
            IsFailure = s.FiscalizationStatus == DesktopSaleFiscalizationStatus.Failed,
            Detail = Truncate(s.FiscalizationStatus == DesktopSaleFiscalizationStatus.Failed ? s.FiscalError : s.LastPostingError)
        }).ToList();
    }

    private async Task<List<LiveTransactionEventDto>> ReadInvoicesAsync(DateTime sinceUtc, int limit, CancellationToken ct)
    {
        var rows = await context.Invoices
            .AsNoTracking()
            .Where(i => i.CreatedAt >= sinceUtc)
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Take(limit + 1)
            .Select(i => new { i.Id, i.SAPDocNum, i.CardCode, i.CardName, i.DocTotal, i.DocCurrency, i.Status, i.SyncedToSAP, i.SyncError, i.CreatedAt })
            .ToListAsync(ct);

        return rows.Select(i => new LiveTransactionEventDto
        {
            EventId = InvoiceEventId(i.Id),
            Kind = LiveTransactionKinds.Invoice,
            OccurredAtUtc = AsUtc(i.CreatedAt),
            Reference = i.SAPDocNum?.ToString(CultureInfo.InvariantCulture),
            Counterparty = i.CardName ?? i.CardCode,
            Amount = i.DocTotal,
            Currency = i.DocCurrency,
            Status = i.Status,
            IsFailure = !i.SyncedToSAP && !string.IsNullOrWhiteSpace(i.SyncError),
            Detail = Truncate(i.SyncError)
        }).ToList();
    }

    private async Task<List<LiveTransactionEventDto>> ReadIncomingPaymentsAsync(DateTime sinceUtc, int limit, CancellationToken ct)
    {
        var rows = await context.IncomingPayments
            .AsNoTracking()
            .Where(p => p.CreatedAt >= sinceUtc)
            .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .Take(limit + 1)
            .Select(p => new { p.Id, p.SAPDocNum, p.CardCode, p.CardName, p.DocTotal, p.DocCurrency, p.Status, p.SyncedToSAP, p.SyncError, p.CreatedAt })
            .ToListAsync(ct);

        return rows.Select(p => new LiveTransactionEventDto
        {
            EventId = $"payment:{p.Id}",
            Kind = LiveTransactionKinds.IncomingPayment,
            OccurredAtUtc = AsUtc(p.CreatedAt),
            Reference = p.SAPDocNum?.ToString(CultureInfo.InvariantCulture),
            Counterparty = p.CardName ?? p.CardCode,
            Amount = p.DocTotal,
            Currency = p.DocCurrency,
            Status = p.Status,
            IsFailure = !p.SyncedToSAP && !string.IsNullOrWhiteSpace(p.SyncError),
            Detail = Truncate(p.SyncError)
        }).ToList();
    }

    /// <summary>
    /// PayNow, Innbucks and EcoCash. Timed at completion when there is one, so a payment that started
    /// before the window but went through inside it still shows — and shows as its outcome.
    /// </summary>
    private async Task<List<LiveTransactionEventDto>> ReadMobilePaymentsAsync(DateTime sinceUtc, int limit, CancellationToken ct)
    {
        var rows = await context.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.CreatedAt >= sinceUtc || p.CompletedAt >= sinceUtc)
            .OrderBy(p => p.CompletedAt ?? p.CreatedAt).ThenBy(p => p.Id)
            .Take(limit + 1)
            .Select(p => new { p.Id, p.Provider, p.PaymentMethod, p.Amount, p.Currency, p.Status, p.StatusMessage, p.Reference, p.CustomerCode, p.CreatedAt, p.CompletedAt })
            .ToListAsync(ct);

        return rows.Select(p => new LiveTransactionEventDto
        {
            // The status is part of the id so that Pending -> Paid arrives as a new event.
            EventId = $"mobile:{p.Id}:{p.Status}",
            Kind = LiveTransactionKinds.MobilePayment,
            OccurredAtUtc = AsUtc(p.CompletedAt ?? p.CreatedAt),
            Reference = p.Reference,
            Counterparty = p.CustomerCode,
            Amount = p.Amount,
            Currency = p.Currency,
            Channel = string.IsNullOrWhiteSpace(p.PaymentMethod) ? p.Provider : $"{p.Provider} {p.PaymentMethod}",
            Status = p.Status,
            IsFailure = p.Status is "Failed" or "Cancelled" or "Expired" or "Error",
            Detail = Truncate(p.StatusMessage)
        }).ToList();
    }

    private async Task<(List<LiveTransactionEventDto>? Events, bool HasMore, string? Message)> ReadFiscalAsync(
        DateTime sinceUtc,
        int limit,
        CancellationToken ct)
    {
        var settings = fiscalisationSettings.Value;
        if (!settings.Enabled)
        {
            return (null, false, "Fiscalisation is turned off in this installation's settings.");
        }

        if (!settings.UsesPlatform)
        {
            return (null, false, "Fiscal activity is only available from the Fiscalisation platform; this installation fiscalises through REVMax.");
        }

        try
        {
            var feed = await fiscalActivityFeed.GetFeedAsync(
                new DateTimeOffset(sinceUtc, TimeSpan.Zero),
                Math.Min(limit, FiscalActivityFeedClient.MaxLimit),
                ct);

            return (feed.Events.Select(MapFiscalEvent).ToList(), feed.HasMore, null);
        }
        catch (FiscalisationApiException ex)
        {
            // Debug, not Warning: the dashboard polls every few seconds and shows the reason on screen,
            // so a missing scope would otherwise write the same warning thousands of times a day.
            logger.LogDebug(ex, "Fiscal activity feed refused: {StatusCode} {ErrorCode}", ex.StatusCode, ex.ErrorCode);
            return (null, false, DescribeRefusal(ex));
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Fiscal activity feed unreachable");
            return (null, false, "The Fiscalisation platform could not be reached.");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Fiscal activity feed timed out");
            return (null, false, "The Fiscalisation platform did not answer in time.");
        }
    }

    internal static string DescribeRefusal(FiscalisationApiException ex) => ex switch
    {
        { ErrorCode: FiscalisationApiClient.ApiKeyNotConfiguredErrorCode } =>
            "No Fiscalisation API key is configured.",
        { StatusCode: HttpStatusCode.Forbidden } =>
            "The Fiscalisation API key does not have the 'activity.read' scope. Grant it on the platform's API Keys page.",
        { StatusCode: HttpStatusCode.Unauthorized } =>
            "The Fiscalisation platform rejected the API key.",
        { StatusCode: HttpStatusCode.NotFound } or { HasProblemDocument: false } =>
            "The Fiscalisation platform does not serve the activity feed yet; it needs a newer release.",
        _ => $"The Fiscalisation platform refused the activity feed: {Truncate(ex.Message)}"
    };

    internal static LiveTransactionEventDto MapFiscalEvent(FiscalActivityEventApiDto e)
    {
        var kind = e.Kind switch
        {
            "Receipt" => LiveTransactionKinds.FiscalReceipt,
            "Attempt" => LiveTransactionKinds.FiscalAttempt,
            _ => LiveTransactionKinds.FiscalDay
        };

        return new LiveTransactionEventDto
        {
            EventId = "fiscal:" + e.EventId,
            Kind = kind,
            OccurredAtUtc = e.OccurredAt.UtcDateTime,
            Reference = kind == LiveTransactionKinds.FiscalDay
                ? e.FiscalDayNo is { } day ? $"Day {day}" : null
                : e.InvoiceNo,
            Counterparty = e.OriginClient,
            Amount = e.Total,
            Currency = e.Currency,
            Location = e.OriginLocation,
            Channel = string.IsNullOrWhiteSpace(e.OriginChannel) ? e.OriginRoute : e.OriginChannel,
            Status = kind == LiveTransactionKinds.FiscalDay && !string.IsNullOrWhiteSpace(e.Action)
                ? $"{e.Action} {e.Status}"
                : e.Status,
            IsFailure = e.IsFailure,
            Detail = Truncate(e.Message ?? e.ErrorCode),
            FiscalDeviceId = e.DeviceId,
            FiscalDayNo = e.FiscalDayNo,
            ReceiptGlobalNo = e.ReceiptGlobalNo,
            ReceiptType = e.ReceiptType
        };
    }

    /// <summary>
    /// Points each fiscal receipt and attempt at the sale or invoice it was for. A till or van sale is
    /// fiscalised under its external reference; a SAP invoice under its DocNum.
    /// </summary>
    private async Task LinkFiscalEventsAsync(List<LiveTransactionEventDto> fiscalEvents, CancellationToken ct)
    {
        var invoiceNumbers = fiscalEvents
            .Where(e => e.Kind != LiveTransactionKinds.FiscalDay && !string.IsNullOrWhiteSpace(e.Reference))
            .Select(e => e.Reference!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (invoiceNumbers.Count == 0)
        {
            return;
        }

        var saleIds = await context.DesktopSales
            .AsNoTracking()
            .Where(s => invoiceNumbers.Contains(s.ExternalReferenceId))
            .Select(s => new { s.Id, s.ExternalReferenceId })
            .ToDictionaryAsync(s => s.ExternalReferenceId, s => s.Id, StringComparer.Ordinal, ct);

        var docNums = invoiceNumbers
            .Where(n => !saleIds.ContainsKey(n))
            .Select(n => int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var docNum) ? docNum : (int?)null)
            .OfType<int>()
            .ToList();

        var invoiceIds = docNums.Count == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : (await context.Invoices
                .AsNoTracking()
                .Where(i => i.SAPDocNum != null && docNums.Contains(i.SAPDocNum.Value))
                .Select(i => new { i.Id, DocNum = i.SAPDocNum!.Value })
                .ToListAsync(ct))
                .GroupBy(i => i.DocNum.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Min(i => i.Id), StringComparer.Ordinal);

        foreach (var e in fiscalEvents)
        {
            if (e.Reference is null || e.Kind == LiveTransactionKinds.FiscalDay)
            {
                continue;
            }

            if (saleIds.TryGetValue(e.Reference, out var saleId))
            {
                e.LinkedEventId = SaleEventId(saleId);
            }
            else if (invoiceIds.TryGetValue(e.Reference, out var invoiceId))
            {
                e.LinkedEventId = InvoiceEventId(invoiceId);
            }
        }
    }

    internal static string SaleEventId(int id) => $"sale:{id}";

    internal static string InvoiceEventId(int id) => $"invoice:{id}";

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string? Truncate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= DetailMaxLength ? value
        : value[..DetailMaxLength] + "…";

    /// <summary>
    /// Merges independently capped sources so that the page never claims more than it read.
    /// </summary>
    internal sealed class PageBuilder(int limit)
    {
        private readonly List<LiveTransactionEventDto> _events = [];
        private DateTime? _cutoff;

        /// <param name="source">Read with <c>Take(limit + 1)</c>; one past the limit means it was capped.</param>
        /// <param name="alreadyCapped">The source said so itself, as the platform's feed does.</param>
        public void Add(List<LiveTransactionEventDto> source, bool alreadyCapped = false)
        {
            var capped = alreadyCapped || source.Count > limit;
            var kept = source
                .OrderBy(e => e.OccurredAtUtc)
                .ThenBy(e => e.EventId, StringComparer.Ordinal)
                .Take(limit)
                .ToList();

            if (capped && kept.Count > 0)
            {
                var last = kept[^1].OccurredAtUtc;
                _cutoff = _cutoff is null || last < _cutoff ? last : _cutoff;
            }

            _events.AddRange(kept);
        }

        public (List<LiveTransactionEventDto> Events, bool HasMore) Build()
        {
            var merged = _events
                .Where(e => _cutoff is null || e.OccurredAtUtc <= _cutoff)
                .OrderBy(e => e.OccurredAtUtc)
                .ThenBy(e => e.EventId, StringComparer.Ordinal)
                .ToList();

            var hasMore = _cutoff is not null || merged.Count > limit;
            return (merged.Take(limit).ToList(), hasMore);
        }
    }
}
