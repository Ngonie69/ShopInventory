using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FiscalisationConfiguration.Commands.AssignOfflineSigningLease;

/// <param name="HolderUserId">
/// The handset user that may sign offline on this device, or null to leave nobody nominated — which stops
/// every van trading out of coverage on it, and is the safe state rather than a broken one.
/// </param>
/// <param name="Force">
/// Move the nomination even though the outgoing holder has signed receipts it has not uploaded, or has
/// never said whether it has. Sometimes the right call — a handset that is lost, broken or stolen is not
/// going to report anything — but it strands whatever it was carrying, so it is never the default.
/// </param>
public sealed record AssignOfflineSigningLeaseCommand(
    int DeviceId,
    Guid? HolderUserId,
    bool Force,
    Guid ActorId,
    string ActorName) : IRequest<ErrorOr<FiscalDeviceOfflineLeaseDto>>;
