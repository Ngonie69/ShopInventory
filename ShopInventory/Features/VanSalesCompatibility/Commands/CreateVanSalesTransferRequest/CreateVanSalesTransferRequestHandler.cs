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

        // The depot the van loads from, from the account rather than the payload. The handset's own
        // warehouse field was a picker over one hardcoded name — "Graniteside Center" — which is not a
        // code SAP knows and was wrong for every Bulawayo van regardless. Whatever it sends is ignored.
        var sourceWarehouseCode = VanSalesCompatibilityMapper.ResolveSupplyingWarehouseCode(user);
        if (string.IsNullOrWhiteSpace(sourceWarehouseCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingSourceWarehouse",
                "This van has no supplying warehouse assigned. Set one on the account before requesting stock.");
        }

        if (string.Equals(sourceWarehouseCode, destinationWarehouseCode, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(
                "VanSalesCompatibility.SourceIsDestination",
                "The van's supplying warehouse is the van itself. Correct the assignment on the account.");
        }

        var transferRequest = VanSalesCompatibilityMapper.MapTransferRequest(
            command.Request,
            user,
            destinationWarehouseCode,
            sourceWarehouseCode);

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