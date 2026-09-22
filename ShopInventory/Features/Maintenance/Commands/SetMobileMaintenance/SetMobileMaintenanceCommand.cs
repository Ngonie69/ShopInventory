using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Maintenance.Commands.SetMobileMaintenance;

/// <param name="Request">What the operator asked for.</param>
/// <param name="UserName">Who asked, for the audit trail and the settings screen.</param>
public sealed record SetMobileMaintenanceCommand(
    SetMobileMaintenanceRequest Request,
    string UserName
) : IRequest<ErrorOr<SetMobileMaintenanceResponse>>;
