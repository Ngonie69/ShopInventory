using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FiscalisationConfiguration.Commands.RegisterFiscalDeviceHandset;

/// <summary>
/// Registers the one handset that signs as a fiscal device, or releases the device from whoever holds it.
/// </summary>
/// <remarks>
/// A null <c>HandsetUserId</c> releases the device rather than registering it. <c>Force</c> releases it
/// even though the handset holding it is still carrying signed receipts, or has never said whether it is
/// — for a handset that is lost or broken.
/// </remarks>
public sealed record RegisterFiscalDeviceHandsetCommand(
    int DeviceId,
    Guid? HandsetUserId,
    bool Force,
    Guid ActorId,
    string ActorName) : IRequest<ErrorOr<FiscalDevicePreviewDto>>;
