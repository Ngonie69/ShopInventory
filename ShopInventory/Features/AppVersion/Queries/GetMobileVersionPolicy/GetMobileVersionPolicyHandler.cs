using ErrorOr;
using MediatR;

namespace ShopInventory.Features.AppVersion.Queries.GetMobileVersionPolicy;

public sealed class GetMobileVersionPolicyHandler(
    IMobileVersionPolicyEvaluator evaluator
) : IRequestHandler<GetMobileVersionPolicyQuery, ErrorOr<MobileVersionPolicyResponse>>
{
    public Task<ErrorOr<MobileVersionPolicyResponse>> Handle(
        GetMobileVersionPolicyQuery request,
        CancellationToken cancellationToken)
    {
        var evaluation = evaluator.Evaluate(request.Platform, request.CurrentVersion);

        ErrorOr<MobileVersionPolicyResponse> result = new MobileVersionPolicyResponse(
            evaluation.Status,
            evaluation.CurrentVersion,
            evaluation.LatestVersion,
            evaluation.RecommendedVersion,
            evaluation.MinimumSupportedVersion,
            evaluation.DownloadUrl,
            evaluation.ReleaseNotes,
            evaluation.Message,
            evaluation.ShouldForceUpgrade,
            evaluation.CheckedAtUtc);

        return Task.FromResult(result);
    }
}