using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditControl.Queries.GetCreditHeadroom;

/// <summary>
/// Serves the credit room left on a set of customers, for a screen that is about to ask someone to
/// approve orders against them.
/// </summary>
/// <remarks>
/// The refusal message an approver eventually sees already carries every figure they need. What it
/// does not do is arrive in time: on 2026-08-20 the same order at SPA077 was pushed four times,
/// each attempt spending between 8 and 26 seconds re-pricing against live SAP before the credit gate
/// refused it, and the fourth attempt — already cut from 1,050.48 to 794.82 — was still 8.75 over.
/// The account had 786.07 of room the whole time.
/// </remarks>
public sealed class GetCreditHeadroomHandler(
    ICreditLimitReviewCache reviewCache,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetCreditHeadroomHandler> logger
) : IRequestHandler<GetCreditHeadroomQuery, ErrorOr<CreditHeadroomResponseDto>>
{
    /// <summary>
    /// A page of orders, not a report. Keeps one caller from turning this into a whole-customer-base
    /// dump through a query string.
    /// </summary>
    private const int MaxCardCodes = 100;

    public async Task<ErrorOr<CreditHeadroomResponseDto>> Handle(
        GetCreditHeadroomQuery request,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.CreditControl.SapDisabled;
        }

        var cardCodes = request.CardCodes
            .Select(code => code?.Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCardCodes)
            .ToList();

        if (cardCodes.Count == 0)
        {
            return new CreditHeadroomResponseDto();
        }

        try
        {
            var cached = await reviewCache.GetAsync(request.Refresh, cancellationToken);
            var review = cached.Review;

            var accounts = new List<CreditHeadroomDto>(cardCodes.Count);
            foreach (var cardCode in cardCodes)
            {
                // Absent means no measurable limit — its own or a parent's. That is not "no room";
                // it is "unlimited", and the two must not be shown alike.
                if (!review.PositionsByCardCode.TryGetValue(cardCode, out var position))
                {
                    accounts.Add(new CreditHeadroomDto { CardCode = cardCode, HasLimit = false });
                    continue;
                }

                accounts.Add(new CreditHeadroomDto
                {
                    CardCode = position.CardCode,
                    HasLimit = true,
                    CreditAccountCardCode = position.CreditAccountCardCode,
                    CreditAccountName = position.CreditAccountName,
                    Currency = position.Currency,
                    IsGroup = position.IsGroup,
                    AccountCount = position.AccountCount,
                    CreditLimit = position.CreditLimit,
                    Exposure = position.Exposure,
                    Headroom = position.Headroom
                });
            }

            return new CreditHeadroomResponseDto
            {
                GeneratedAt = cached.GeneratedAtUtc,
                FromCache = cached.FromCache,
                Accounts = accounts
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read credit headroom for {CardCodeCount} customer(s)", cardCodes.Count);
            return Errors.CreditControl.ReviewFailed(ex.Message);
        }
    }
}
