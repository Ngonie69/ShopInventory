using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.PostingDatePolicy.Commands.UpdatePostingDatePolicy;

public sealed class UpdatePostingDatePolicyHandler(HttpClient httpClient, ILogger<UpdatePostingDatePolicyHandler> logger)
    : IRequestHandler<UpdatePostingDatePolicyCommand, ErrorOr<PostingDatePolicySettings>>
{
    public Task<ErrorOr<PostingDatePolicySettings>> Handle(UpdatePostingDatePolicyCommand request, CancellationToken cancellationToken) =>
        PostingDatePolicyApi.SendAsync<PostingDatePolicySettings>(
            httpClient, logger, HttpMethod.Put, new { request.AllowCustomPostingDate },
            "change the posting date setting", cancellationToken);
}
