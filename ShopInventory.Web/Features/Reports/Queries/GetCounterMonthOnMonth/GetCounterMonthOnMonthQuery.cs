using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetCounterMonthOnMonth;

public sealed record GetCounterMonthOnMonthQuery(DateTime PreviousMonth, DateTime CurrentMonth)
    : IRequest<ErrorOr<CounterMonthOnMonthData>>;
