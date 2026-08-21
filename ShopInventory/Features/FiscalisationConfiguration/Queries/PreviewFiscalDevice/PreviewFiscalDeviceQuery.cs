using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.PreviewFiscalDevice;

/// <summary>
/// Asks the Fiscalisation platform what a device is, before anyone is registered against it.
/// </summary>
/// <remarks>
/// <c>HandsetUserId</c> is the van it is intended for, when one has been chosen. Optional: the office
/// types an id first and picks the handset second, and the device's own problems are worth reporting
/// either way.
/// </remarks>
public sealed record PreviewFiscalDeviceQuery(int DeviceId, Guid? HandsetUserId)
    : IRequest<ErrorOr<FiscalDevicePreviewDto>>;
