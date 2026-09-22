using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Maintenance.Commands.SetMaintenance;

/// <param name="Request">What the operator asked for.</param>
/// <param name="UserName">Who asked, for the audit trail and the settings screen.</param>
public sealed record SetMaintenanceCommand(
    SetMaintenanceRequest Request,
    string UserName
) : IRequest<ErrorOr<SetMaintenanceResponse>>;
