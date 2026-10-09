using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.LiveTransactions.Queries.GetLiveTransactionFeed;

/// <param name="SinceUtc">Events at or after this instant. Null means the start of today, CAT.</param>
/// <param name="Limit">Page size across all sources together.</param>
public sealed record GetLiveTransactionFeedQuery(
    DateTime? SinceUtc,
    int Limit = GetLiveTransactionFeedQuery.DefaultLimit
) : IRequest<ErrorOr<LiveTransactionFeedDto>>
{
    public const int DefaultLimit = 300;
    public const int MaxLimit = 1000;

    /// <summary>A live view, not a report: anything older belongs on the reports pages.</summary>
    public static readonly TimeSpan MaxLookback = TimeSpan.FromDays(7);
}
