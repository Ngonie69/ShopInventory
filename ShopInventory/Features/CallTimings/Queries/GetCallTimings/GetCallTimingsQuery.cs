using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>The API's recent inbound requests and SAP calls, summarised over <paramref name="Window"/>.</summary>
/// <param name="Window">One of <see cref="CallTimingWindows.All"/>: <c>15m</c>, <c>1h</c> or <c>today</c> (since midnight CAT).</param>
public sealed record GetCallTimingsQuery(string Window = CallTimingWindows.Today) : IRequest<ErrorOr<GetCallTimingsResult>>;
