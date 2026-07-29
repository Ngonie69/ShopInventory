using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditControl.Queries.GetCreditLimitReview;

/// <param name="Refresh">
/// Forces a fresh SAP sweep instead of the cached result. Use after taking a payment, when the
/// point is to confirm the account is back under its limit.
/// </param>
public sealed record GetCreditLimitReviewQuery(bool Refresh = false)
    : IRequest<ErrorOr<CreditLimitReviewDto>>;
