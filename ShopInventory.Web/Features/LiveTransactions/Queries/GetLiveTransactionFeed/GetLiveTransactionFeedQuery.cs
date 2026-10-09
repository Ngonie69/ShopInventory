using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.LiveTransactions.Queries.GetLiveTransactionFeed;

public sealed record GetLiveTransactionFeedQuery(DateTime? SinceUtc, int Limit = 500)
    : IRequest<ErrorOr<LiveTransactionFeedModel>>;
