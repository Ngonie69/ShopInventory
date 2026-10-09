using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.LiveTransactions.Queries.GetLiveTransactionFeed;

public sealed class GetLiveTransactionFeedHandler(ILiveTransactionService liveTransactionService)
    : IRequestHandler<GetLiveTransactionFeedQuery, ErrorOr<LiveTransactionFeedModel>>
{
    public async Task<ErrorOr<LiveTransactionFeedModel>> Handle(
        GetLiveTransactionFeedQuery request,
        CancellationToken cancellationToken)
    {
        var feed = await liveTransactionService.GetFeedAsync(request.SinceUtc, request.Limit, cancellationToken);
        return feed is null ? Errors.LiveTransactions.LoadFailed : feed;
    }
}
