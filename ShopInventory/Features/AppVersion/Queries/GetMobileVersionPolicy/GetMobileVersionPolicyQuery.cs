using ErrorOr;
using MediatR;

namespace ShopInventory.Features.AppVersion.Queries.GetMobileVersionPolicy;

public sealed record GetMobileVersionPolicyQuery(string? AppId, string? Platform, string? CurrentVersion)
    : IRequest<ErrorOr<MobileVersionPolicyResponse>>;