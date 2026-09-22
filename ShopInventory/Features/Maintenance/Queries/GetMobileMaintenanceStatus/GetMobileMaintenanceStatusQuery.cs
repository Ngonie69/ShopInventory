using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Maintenance.Queries.GetMobileMaintenanceStatus;

/// <summary>
/// What an app asks so it can show a banner rather than find out by being refused.
/// </summary>
/// <param name="AppId">The caller's app id, from its header or the query string.</param>
public sealed record GetMobileMaintenanceStatusQuery(string? AppId) : IRequest<ErrorOr<MobileMaintenanceStatusDto>>;
