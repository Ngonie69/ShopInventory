using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.PostVanSalesAttendance;

public sealed record PostVanSalesAttendanceCommand(
    VanSalesAttendanceRequest Request,
    Guid UserId) : IRequest<ErrorOr<VanSalesAttendanceCheckResponse>>;