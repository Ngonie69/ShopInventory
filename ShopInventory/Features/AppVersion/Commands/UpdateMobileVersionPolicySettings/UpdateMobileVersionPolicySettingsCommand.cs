using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.AppVersion.Commands.UpdateMobileVersionPolicySettings;

public sealed record UpdateMobileVersionPolicySettingsCommand(
    UpdateMobileVersionPolicySettingsRequest Request,
    string UserName
) : IRequest<ErrorOr<UpdateMobileVersionPolicySettingsResult>>;