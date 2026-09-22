using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Maintenance.Queries.GetMobileMaintenanceSettings;

/// <summary>The lockout as an operator screen needs to see it.</summary>
public sealed record GetMobileMaintenanceSettingsQuery : IRequest<ErrorOr<MobileMaintenanceSettingsDto>>;
