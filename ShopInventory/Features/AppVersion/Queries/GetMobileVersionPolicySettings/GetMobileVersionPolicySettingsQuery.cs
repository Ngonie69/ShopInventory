using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.AppVersion.Queries.GetMobileVersionPolicySettings;

public sealed record GetMobileVersionPolicySettingsQuery(string? AppId) : IRequest<ErrorOr<MobileVersionPolicySettingsDto>>;