using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.CreateTransferRequest;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesTransferRequest;

public sealed class CreateVanSalesTransferRequestHandler(
    ApplicationDbContext db,
    IMediator mediator
) : IRequestHandler<CreateVanSalesTransferRequestCommand, ErrorOr<VanSalesTransferRequestResponse>>
{
    public async Task<ErrorOr<VanSalesTransferRequestResponse>> Handle(
        CreateVanSalesTransferRequestCommand command,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var destinationWarehouseCode = VanSalesCompatibilityMapper.ResolveAssignedWarehouseCode(user);
        if (string.IsNullOrWhiteSpace(destinationWarehouseCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingWarehouse",
                "An assigned destination warehouse is required for stock transfer requests.");
        }

        if (string.IsNullOrWhiteSpace(command.Request.Warehouse))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingSourceWarehouse",
                "A source warehouse is required for stock transfer requests.");
        }

        var transferRequest = VanSalesCompatibilityMapper.MapTransferRequest(
            command.Request,
            user,
            destinationWarehouseCode);

        var result = await mediator.Send(
            new CreateTransferRequestCommand(transferRequest, command.UserId.ToString()),
            cancellationToken);

        if (result.IsError)
        {
            return result.Errors;
        }

        return VanSalesCompatibilityMapper.MapTransferResponse(result.Value);
    }
}