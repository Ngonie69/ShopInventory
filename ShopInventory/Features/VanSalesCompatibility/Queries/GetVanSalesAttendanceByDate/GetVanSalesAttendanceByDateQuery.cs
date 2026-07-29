using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendanceByDate;

public sealed record GetVanSalesAttendanceByDateQuery(
    Guid UserId,
    string DateValue) : IRequest<ErrorOr<VanSalesAttendanceByDateResponse>>;