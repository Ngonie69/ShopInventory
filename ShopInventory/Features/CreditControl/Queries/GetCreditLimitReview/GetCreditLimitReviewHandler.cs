using ErrorOr;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditControl.Queries.GetCreditLimitReview;

/// <summary>
/// Serves the over-limit list on demand, from a short-lived cache.
/// </summary>
/// <remarks>
/// The sweep behind this reads every customer from SAP, so it is not something to run once per
/// page load. The cache is short — balances move as payments post, and an hour-old answer would
/// have credit control chasing accounts that have already paid — and it is refreshable for the one
/// case that matters: confirming an account is back under its limit after taking payment.
/// </remarks>
public sealed class GetCreditLimitReviewHandler(
    ICreditLimitReviewService reviewService,
    IMemoryCache cache,
    IOptions<SAPSettings> sapSettings,
    IOptions<CreditLimitSettings> creditLimitSettings,
    ILogger<GetCreditLimitReviewHandler> logger
) : IRequestHandler<GetCreditLimitReviewQuery, ErrorOr<CreditLimitReviewDto>>
{
    private const string CacheKey = "credit-control:over-limit-review";

    // Static: the handler is resolved per request, and the point is that ten people opening the
    // page together produce one SAP sweep, not ten.
    private static readonly SemaphoreSlim SweepGate = new(1, 1);

    public async Task<ErrorOr<CreditLimitReviewDto>> Handle(
        GetCreditLimitReviewQuery request,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.CreditControl.SapDisabled;
        }

        if (!request.Refresh && cache.TryGetValue(CacheKey, out CreditLimitReviewDto? cached) && cached != null)
        {
            return WithCacheFlag(cached, fromCache: true);
        }

        await SweepGate.WaitAsync(cancellationToken);
        try
        {
            // Someone else may have swept while this request waited. Honour an explicit refresh,
            // but otherwise take their answer rather than reading all of SAP again.
            if (!request.Refresh && cache.TryGetValue(CacheKey, out CreditLimitReviewDto? sweptMeanwhile) && sweptMeanwhile != null)
            {
                return WithCacheFlag(sweptMeanwhile, fromCache: true);
            }

            var review = await reviewService.ReviewAsync(cancellationToken);
            var dto = MapToDto(review);

            cache.Set(CacheKey, dto, TimeSpan.FromMinutes(Math.Max(1, creditLimitSettings.Value.ReviewCacheMinutes)));

            return WithCacheFlag(dto, fromCache: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build the credit limit review");
            return Errors.CreditControl.ReviewFailed(ex.Message);
        }
        finally
        {
            SweepGate.Release();
        }
    }

    private static CreditLimitReviewDto WithCacheFlag(CreditLimitReviewDto dto, bool fromCache) =>
        new()
        {
            GeneratedAt = dto.GeneratedAt,
            FromCache = fromCache,
            CustomersRead = dto.CustomersRead,
            LimitsMeasured = dto.LimitsMeasured,
            BreachCount = dto.BreachCount,
            TotalOver = dto.TotalOver,
            Breaches = dto.Breaches
        };

    private static CreditLimitReviewDto MapToDto(CreditLimitReview review) => new()
    {
        GeneratedAt = DateTime.UtcNow,
        CustomersRead = review.CustomersRead,
        LimitsMeasured = review.LimitsMeasured,
        BreachCount = review.Breaches.Count,
        TotalOver = review.TotalOver,
        Breaches = review.Breaches
            .Select(breach => new CreditLimitBreachDto
            {
                CardCode = breach.CardCode,
                CardName = breach.CardName,
                Currency = breach.Currency,
                IsGroup = breach.IsGroup,
                AccountCount = breach.AccountCount,
                CreditLimit = breach.CreditLimit,
                Balance = breach.Balance,
                OpenOrders = breach.OpenOrders,
                Exposure = breach.Exposure,
                AmountOver = breach.AmountOver
            })
            .ToList()
    };
}
