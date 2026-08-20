using ErrorOr;
using MediatR;
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
    ICreditLimitReviewCache reviewCache,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetCreditLimitReviewHandler> logger
) : IRequestHandler<GetCreditLimitReviewQuery, ErrorOr<CreditLimitReviewDto>>
{
    public async Task<ErrorOr<CreditLimitReviewDto>> Handle(
        GetCreditLimitReviewQuery request,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.CreditControl.SapDisabled;
        }

        try
        {
            var cached = await reviewCache.GetAsync(request.Refresh, cancellationToken);
            var dto = MapToDto(cached.Review);
            dto.GeneratedAt = cached.GeneratedAtUtc;
            dto.FromCache = cached.FromCache;
            return dto;
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
    }

    private static CreditLimitReviewDto MapToDto(CreditLimitReview review) => new()
    {
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
