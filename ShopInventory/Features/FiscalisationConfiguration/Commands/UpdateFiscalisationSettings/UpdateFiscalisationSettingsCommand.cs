using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FiscalisationConfiguration.Commands.UpdateFiscalisationSettings;

public sealed record UpdateFiscalisationSettingsResult(
    string Message,
    bool? ConnectionTestPassed,
    string? ApiKeyMasked
);

public sealed record UpdateFiscalisationSettingsCommand(
    UpdateFiscalisationSettingsRequest Request,
    string UserName
) : IRequest<ErrorOr<UpdateFiscalisationSettingsResult>>;
