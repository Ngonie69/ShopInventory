using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesAttendance.Queries.GetActiveVanVisit;

/// <summary>
/// The van sales call this rep is currently inside, if any.
///
/// The merchandiser's <c>GetActiveCheckInQuery</c> is pinned to its own channel and would report a
/// van rep as having no open visit at all — which the handset reads as "not checked in" and offers a
/// second check-in for a shop they are standing in.
/// </summary>
public sealed record GetActiveVanVisitQuery(
    Guid UserId
) : IRequest<ErrorOr<ActiveVanVisitResult>>;

public sealed record ActiveVanVisitResult(
    int Id,
    string CustomerCode,
    string CustomerName,
    DateTime CheckInTime,
    double? Latitude,
    double? Longitude,
    string? Notes
);
