using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShopInventory.Web.Services;

public interface IIncomingPaymentCacheService
{
    /// <summary>
    /// Gets cached payments with pagination. Returns cached data immediately if available,
    /// and triggers background sync if cache is stale.
    /// </summary>
    Task<IncomingPaymentListResponse?> GetCachedPaymentsAsync(int page = 1, int pageSize = 20);

    /// <summary>
    /// Gets cached payments by date range
    /// </summary>
    Task<IncomingPaymentDateResponse?> GetCachedPaymentsByDateRangeAsync(DateTime fromDate, DateTime toDate);

    /// <summary>
    /// Gets a single payment by DocEntry
    /// </summary>
    Task<IncomingPaymentDto?> GetCachedPaymentByDocEntryAsync(int docEntry);

    /// <summary>
    /// Forces a full sync of payment data
    /// </summary>
    Task<bool> SyncPaymentsAsync();

    /// <summary>
    /// Gets the sync status for payments
    /// </summary>
    Task<CacheSyncInfo?> GetSyncStatusAsync();

    /// <summary>
    /// Event raised when background sync completes
    /// </summary>
    event EventHandler? SyncCompleted;
}

public class IncomingPaymentCacheService : IIncomingPaymentCacheService
{
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<IncomingPaymentCacheService> _logger;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static bool _syncInProgress;
    private static readonly object _syncLock = new();
    private static readonly SemaphoreSlim _backgroundSyncSemaphore = new(1, 1);
    private const string CacheKey = "IncomingPayments";

    public event EventHandler? SyncCompleted;

    public IncomingPaymentCacheService(
        IDbContextFactory<WebAppDbContext> dbContextFactory,
        HttpClient httpClient,
        ILogger<IncomingPaymentCacheService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IncomingPaymentListResponse?> GetCachedPaymentsAsync(int page = 1, int pageSize = 20)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var syncInfo = await dbContext.CacheSyncInfo.FindAsync(CacheKey);
        var isCacheStale = syncInfo == null ||
                          (DateTime.UtcNow - syncInfo.LastSyncedAt) > _cacheExpiration ||
                          !syncInfo.SyncSuccessful;

        // Get cached count
        var cachedCount = await dbContext.CachedIncomingPayments.CountAsync();

        if (cachedCount > 0)
        {
            // Return cached data
            var skip = (page - 1) * pageSize;
            var cachedItems = await dbContext.CachedIncomingPayments
                .OrderByDescending(p => p.DocDate)
                .ThenByDescending(p => p.DocNum)
                .Skip(skip)
                .Take(pageSize)
                .ToListAsync();

            var response = new IncomingPaymentListResponse
            {
                Page = page,
                PageSize = pageSize,
                Count = cachedItems.Count,
                HasMore = skip + cachedItems.Count < cachedCount,
                Payments = cachedItems.Select(MapCachedToDto).ToList()
            };

            // Trigger background sync if cache is stale
            if (isCacheStale)
            {
                _ = Task.Run(async () =>
                {
                    await _backgroundSyncSemaphore.WaitAsync();
                    try
                    {
                        await SyncPaymentsInBackgroundAsync();
                    }
                    finally
                    {
                        _backgroundSyncSemaphore.Release();
                    }
                });
            }

            return response;
        }

        // No cached data - fetch first page from API and start background sync
        _logger.LogInformation("No cached payments, fetching from API");

        try
        {
            var apiResponse = await FetchPaymentsFromApiAsync(page, pageSize);
            if (apiResponse?.Payments?.Any() == true)
            {
                // Save first page to cache (don't let cache save failure discard API results)
                try { await SavePaymentsToCacheAsync(apiResponse.Payments); }
                catch (Exception saveEx) { _logger.LogWarning(saveEx, "Failed to cache payments"); }

                // Start background sync for remaining items
                _ = Task.Run(async () =>
                {
                    await _backgroundSyncSemaphore.WaitAsync();
                    try
                    {
                        await SyncRemainingPaymentsInBackgroundAsync(apiResponse.HasMore);
                    }
                    finally
                    {
                        _backgroundSyncSemaphore.Release();
                    }
                });

                return apiResponse;
            }
        }
        catch (TimeoutException)
        {
            // Re-throw timeout exceptions so the UI can show a user-friendly message
            throw;
        }
        catch (HttpRequestException)
        {
            // Propagate HTTP errors so the UI can show a meaningful message
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching payments from API");
            throw;
        }

        // API returned no payments - return empty response (not null)
        return new IncomingPaymentListResponse
        {
            Page = page,
            PageSize = pageSize,
            Count = 0,
            HasMore = false,
            Payments = new List<IncomingPaymentDto>()
        };
    }

    public async Task<IncomingPaymentDateResponse?> GetCachedPaymentsByDateRangeAsync(DateTime fromDate, DateTime toDate)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Ensure dates are UTC for PostgreSQL compatibility
        var fromDateUtc = fromDate.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(fromDate, DateTimeKind.Utc) : fromDate.ToUniversalTime();
        var toDateEndUtc = (toDate.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(toDate, DateTimeKind.Utc) : toDate.ToUniversalTime()).Date.AddDays(1);

        var payments = await dbContext.CachedIncomingPayments
            .Where(p => p.DocDate >= fromDateUtc && p.DocDate < toDateEndUtc)
            .OrderByDescending(p => p.DocDate)
            .ThenByDescending(p => p.DocNum)
            .ToListAsync();

        return new IncomingPaymentDateResponse
        {
            FromDate = fromDate.ToString("yyyy-MM-dd"),
            ToDate = toDate.ToString("yyyy-MM-dd"),
            Count = payments.Count,
            Payments = payments.Select(MapCachedToDto).ToList()
        };
    }

    public async Task<IncomingPaymentDto?> GetCachedPaymentByDocEntryAsync(int docEntry)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var cached = await dbContext.CachedIncomingPayments
            .FirstOrDefaultAsync(p => p.DocEntry == docEntry);

        if (cached != null)
        {
            return MapCachedToDto(cached);
        }

        // Try to fetch from API
        try
        {
            var payment = await _httpClient.GetFromJsonAsync<IncomingPaymentDto>(
                $"api/incomingpayment/{docEntry}", _jsonOptions);

            if (payment != null)
            {
                try { await SavePaymentsToCacheAsync(new List<IncomingPaymentDto> { payment }); }
                catch (Exception saveEx) { _logger.LogWarning(saveEx, "Failed to cache payment {DocEntry}", docEntry); }
            }

            return payment;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching payment {DocEntry} from API", docEntry);
            return null;
        }
    }

    public async Task<bool> SyncPaymentsAsync()
    {
        return await SyncPaymentsInBackgroundAsync();
    }

    public async Task<CacheSyncInfo?> GetSyncStatusAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.CacheSyncInfo.FindAsync(CacheKey);
    }

    private async Task<bool> SyncPaymentsInBackgroundAsync()
    {
        lock (_syncLock)
        {
            if (_syncInProgress)
            {
                _logger.LogDebug("Payment sync already in progress");
                return false;
            }
            _syncInProgress = true;
        }

        try
        {
            _logger.LogInformation("Starting full payment sync");

            var allPayments = new List<IncomingPaymentDto>();
            var page = 1;
            var pageSize = 100;
            var hasMore = true;

            while (hasMore)
            {
                var response = await FetchPaymentsFromApiAsync(page, pageSize);
                if (response?.Payments != null)
                {
                    allPayments.AddRange(response.Payments);
                    hasMore = response.HasMore;
                    page++;
                }
                else
                {
                    hasMore = false;
                }
            }

            if (allPayments.Any())
            {
                await ReplacePaymentCacheAsync(allPayments);
                await UpdateSyncInfoAsync(allPayments.Count, true, null);
                _logger.LogInformation("Completed payment sync: {Count} payments", allPayments.Count);
                SyncCompleted?.Invoke(this, EventArgs.Empty);
                return true;
            }
            else
            {
                await UpdateSyncInfoAsync(0, true, "No payments found");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during payment sync");
            await UpdateSyncInfoAsync(0, false, ex.Message);
            return false;
        }
        finally
        {
            lock (_syncLock)
            {
                _syncInProgress = false;
            }
        }
    }

    private async Task SyncRemainingPaymentsInBackgroundAsync(bool hasMore)
    {
        if (!hasMore) return;

        lock (_syncLock)
        {
            if (_syncInProgress)
            {
                _logger.LogDebug("Payment sync already in progress");
                return;
            }
            _syncInProgress = true;
        }

        try
        {
            _logger.LogInformation("Starting background sync for remaining payments");

            var page = 2;
            var pageSize = 100;

            while (hasMore)
            {
                var response = await FetchPaymentsFromApiAsync(page, pageSize);
                if (response?.Payments != null && response.Payments.Any())
                {
                    await SavePaymentsToCacheAsync(response.Payments);
                    hasMore = response.HasMore;
                    page++;
                    _logger.LogDebug("Cached page {Page}: {Count} payments", page - 1, response.Payments.Count);
                }
                else
                {
                    hasMore = false;
                }
            }

            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var totalCount = await dbContext.CachedIncomingPayments.CountAsync();

            await UpdateSyncInfoAsync(totalCount, true, null);
            _logger.LogInformation("Completed background payment sync: {Count} total payments", totalCount);
            SyncCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during background payment sync");
            await UpdateSyncInfoAsync(0, false, ex.Message);
        }
        finally
        {
            lock (_syncLock)
            {
                _syncInProgress = false;
            }
        }
    }

    private async Task<IncomingPaymentListResponse?> FetchPaymentsFromApiAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        try
        {
            // Use a 90-second timeout to allow for backend retries against SAP
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(90));

            return await _httpClient.GetFromJsonAsync<IncomingPaymentListResponse>(
                $"api/incomingpayment?page={page}&pageSize={pageSize}", _jsonOptions, cts.Token);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken != cancellationToken)
        {
            _logger.LogWarning("Timeout fetching payments from API, page {Page} - SAP may be slow", page);
            throw new TimeoutException($"The request timed out while fetching payments. SAP may be responding slowly. Please try again.", ex);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("504") || ex.Message.Contains("Gateway") || ex.Message.Contains("502"))
        {
            _logger.LogWarning(ex, "Gateway error fetching payments from API, page {Page}", page);
            throw new TimeoutException($"Gateway error while fetching payments. The server may be busy. Please try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching payments from API, page {Page}", page);
            throw; // Propagate so the caller can show a meaningful error
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching payments from API, page {Page}", page);
            return null;
        }
    }

    private async Task SavePaymentsToCacheAsync(List<IncomingPaymentDto> payments)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        foreach (var payment in payments)
        {
            var existing = await dbContext.CachedIncomingPayments
                .FirstOrDefaultAsync(p => p.DocEntry == payment.DocEntry);

            var cached = existing ?? new CachedIncomingPayment();
            MapDtoToCached(payment, cached);
            cached.LastSyncedAt = now;

            if (existing == null)
            {
                dbContext.CachedIncomingPayments.Add(cached);
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task ReplacePaymentCacheAsync(List<IncomingPaymentDto> payments)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Delete all existing cached payments
        await dbContext.CachedIncomingPayments.ExecuteDeleteAsync();

        // Add all new items
        var now = DateTime.UtcNow;
        var cachedPayments = payments.Select(payment =>
        {
            var cached = new CachedIncomingPayment();
            MapDtoToCached(payment, cached);
            cached.LastSyncedAt = now;
            return cached;
        }).ToList();

        dbContext.CachedIncomingPayments.AddRange(cachedPayments);
        await dbContext.SaveChangesAsync();
    }

    private async Task UpdateSyncInfoAsync(int count, bool success, string? error)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var syncInfo = await dbContext.CacheSyncInfo.FindAsync(CacheKey);
        if (syncInfo == null)
        {
            syncInfo = new CacheSyncInfo { CacheKey = CacheKey };
            dbContext.CacheSyncInfo.Add(syncInfo);
        }

        syncInfo.LastSyncedAt = DateTime.UtcNow;
        syncInfo.ItemCount = count;
        syncInfo.SyncSuccessful = success;
        syncInfo.LastError = error;

        await dbContext.SaveChangesAsync();
    }

    private static void MapDtoToCached(IncomingPaymentDto dto, CachedIncomingPayment cached)
    {
        cached.DocEntry = dto.DocEntry;
        cached.DocNum = dto.DocNum;
        cached.DocDate = DateTime.TryParse(dto.DocDate, out var docDate) ? DateTime.SpecifyKind(docDate, DateTimeKind.Utc) : null;
        cached.DocDueDate = DateTime.TryParse(dto.DocDueDate, out var dueDate) ? DateTime.SpecifyKind(dueDate, DateTimeKind.Utc) : null;
        cached.CardCode = dto.CardCode;
        cached.CardName = dto.CardName;
        cached.DocCurrency = dto.DocCurrency;
        cached.CashSum = dto.CashSum;
        cached.CheckSum = dto.CheckSum;
        cached.TransferSum = dto.TransferSum;
        cached.CreditSum = dto.CreditSum;
        cached.DocTotal = dto.DocTotal;
        cached.Remarks = dto.Remarks;
        cached.TransferReference = dto.TransferReference;
        cached.TransferDate = DateTime.TryParse(dto.TransferDate, out var transferDate) ? DateTime.SpecifyKind(transferDate, DateTimeKind.Utc) : null;
        cached.TransferAccount = dto.TransferAccount;
        cached.PaymentInvoicesJson = dto.PaymentInvoices != null ? JsonSerializer.Serialize(dto.PaymentInvoices) : null;
        cached.PaymentChecksJson = dto.PaymentChecks != null ? JsonSerializer.Serialize(dto.PaymentChecks) : null;
        cached.PaymentCreditCardsJson = dto.PaymentCreditCards != null ? JsonSerializer.Serialize(dto.PaymentCreditCards) : null;
    }

    private static IncomingPaymentDto MapCachedToDto(CachedIncomingPayment cached)
    {
        return new IncomingPaymentDto
        {
            DocEntry = cached.DocEntry,
            DocNum = cached.DocNum,
            DocDate = cached.DocDate?.ToString("yyyy-MM-dd"),
            DocDueDate = cached.DocDueDate?.ToString("yyyy-MM-dd"),
            CardCode = cached.CardCode,
            CardName = cached.CardName,
            DocCurrency = cached.DocCurrency,
            CashSum = cached.CashSum,
            CheckSum = cached.CheckSum,
            TransferSum = cached.TransferSum,
            CreditSum = cached.CreditSum,
            DocTotal = cached.DocTotal,
            Remarks = cached.Remarks,
            TransferReference = cached.TransferReference,
            TransferDate = cached.TransferDate?.ToString("yyyy-MM-dd"),
            TransferAccount = cached.TransferAccount,
            PaymentInvoices = !string.IsNullOrEmpty(cached.PaymentInvoicesJson)
                ? JsonSerializer.Deserialize<List<PaymentInvoiceDto>>(cached.PaymentInvoicesJson, _jsonOptions)
                : null,
            PaymentChecks = !string.IsNullOrEmpty(cached.PaymentChecksJson)
                ? JsonSerializer.Deserialize<List<PaymentCheckDto>>(cached.PaymentChecksJson, _jsonOptions)
                : null,
            PaymentCreditCards = !string.IsNullOrEmpty(cached.PaymentCreditCardsJson)
                ? JsonSerializer.Deserialize<List<PaymentCreditCardDto>>(cached.PaymentCreditCardsJson, _jsonOptions)
                : null
        };
    }
}
