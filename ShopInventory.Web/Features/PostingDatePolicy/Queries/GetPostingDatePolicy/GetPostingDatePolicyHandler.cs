using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.PostingDatePolicy.Queries.GetPostingDatePolicy;

public sealed class GetPostingDatePolicyHandler(HttpClient httpClient, ILogger<GetPostingDatePolicyHandler> logger)
    : IRequestHandler<GetPostingDatePolicyQuery, ErrorOr<PostingDatePolicySettings>>
{
    public Task<ErrorOr<PostingDatePolicySettings>> Handle(GetPostingDatePolicyQuery request, CancellationToken cancellationToken) =>
        PostingDatePolicyApi.SendAsync<PostingDatePolicySettings>(
            httpClient, logger, HttpMethod.Get, null, "load the posting date setting", cancellationToken);
}
