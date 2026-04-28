using ErrorOr;
using MediatR;

namespace ShopInventory.Features.AppVersion.Queries.GetMobileVersionPolicy;

public sealed record GetMobileVersionPolicyQuery(string? Platform, string? CurrentVersion)
    : IRequest<ErrorOr<MobileVersionPolicyResponse>>;