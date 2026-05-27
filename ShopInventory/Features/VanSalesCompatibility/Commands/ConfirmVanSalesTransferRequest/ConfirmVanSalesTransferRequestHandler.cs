using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.InventoryTransfers.Commands.CloseTransferRequest;
using ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.ConfirmVanSalesTransferRequest;

public sealed class ConfirmVanSalesTransferRequestHandler(
    ApplicationDbContext db,
    IMediator mediator
) : IRequestHandler<ConfirmVanSalesTransferRequestCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        ConfirmVanSalesTransferRequestCommand command,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        if (command.Request.Id <= 0)
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidTransferRequest",
                "A valid transfer request is required.");
        }

        return command.Request.Status switch
        {
            2 => await ConvertAsync(command.Request.Id, cancellationToken),
            3 => await CloseAsync(command.Request.Id, cancellationToken),
            _ => Error.Validation(
                "VanSalesCompatibility.InvalidTransferStatus",
                "Only approve (2) and reject (3) transfer request actions are supported.")
        };
    }

    private async Task<ErrorOr<string>> ConvertAsync(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ConvertTransferRequestCommand(docEntry), cancellationToken);
        if (result.IsError)
        {
            return result.Errors;
        }

        var transferDocNum = result.Value.Transfer?.DocNum;
        return transferDocNum.HasValue
            ? $"Transfer request approved and converted to inventory transfer {transferDocNum.Value}."
            : "Transfer request approved successfully.";
    }

    private async Task<ErrorOr<string>> CloseAsync(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CloseTransferRequestCommand(docEntry), cancellationToken);
        if (result.IsError)
        {
            return result.Errors;
        }

        return $"Transfer request {docEntry} rejected successfully.";
    }
}