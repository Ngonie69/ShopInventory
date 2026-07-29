using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendanceStatus;

public sealed record GetVanSalesAttendanceStatusQuery(
    Guid UserId) : IRequest<ErrorOr<VanSalesAttendanceStatusResponse>>;