using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Maintenance.Queries.GetMaintenanceSettings;

/// <summary>The lockout as an operator screen needs to see it.</summary>
public sealed record GetMaintenanceSettingsQuery : IRequest<ErrorOr<MaintenanceSettingsDto>>;
