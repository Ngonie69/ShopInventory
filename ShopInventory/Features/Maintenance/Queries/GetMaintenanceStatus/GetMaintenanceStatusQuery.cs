using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Maintenance.Queries.GetMaintenanceStatus;

/// <summary>
/// What a client asks so it can show a banner rather than find out by being refused.
/// </summary>
/// <param name="Caller">
/// The audience the request belongs to, as classified from its own headers, and the mobile app it
/// named if it is a phone. Asking on the caller's behalf rather than globally is the point: a
/// lockout aimed at the phones must not make the web portal put up a banner.
/// </param>
public sealed record GetMaintenanceStatusQuery(MaintenanceCaller Caller) : IRequest<ErrorOr<MaintenanceStatusDto>>;
