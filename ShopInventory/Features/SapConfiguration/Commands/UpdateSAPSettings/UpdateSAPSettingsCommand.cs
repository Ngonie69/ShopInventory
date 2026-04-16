using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SapConfiguration.Commands.UpdateSAPSettings;

public sealed record UpdateSAPSettingsResult(
    string Message,
    bool? ConnectionTestPassed
);

public sealed record UpdateSAPSettingsCommand(
    UpdateSAPSettingsRequest Request,
    string UserName
) : IRequest<ErrorOr<UpdateSAPSettingsResult>>;
