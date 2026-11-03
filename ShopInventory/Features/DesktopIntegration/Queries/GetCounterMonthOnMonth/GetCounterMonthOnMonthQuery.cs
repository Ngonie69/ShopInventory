using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetCounterMonthOnMonth;

/// <summary>
/// Two calendar months of shop and vending sales side by side: by place, tender and item.
/// </summary>
/// <param name="PreviousMonth">Any day in the earlier month; only its year and month are used.</param>
/// <param name="CurrentMonth">Any day in the later month; only its year and month are used.</param>
public sealed record GetCounterMonthOnMonthQuery(
    Guid CallerUserId,
    DateTime PreviousMonth,
    DateTime CurrentMonth) : IRequest<ErrorOr<CounterMonthOnMonthResult>>;
