using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendance;

public sealed record GetVanSalesAttendanceQuery(
    Guid UserId) : IRequest<ErrorOr<VanSalesAttendanceListResponse>>;