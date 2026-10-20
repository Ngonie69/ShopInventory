using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.FactoryTransfers.Commands.CancelFactoryTransfer;

/// <summary>
/// The sender abandons a transfer whose send never reached SAP.
/// </summary>
/// <remarks>
/// "Failed" is not proof that nothing moved: a send whose reply was lost reads exactly the same. So
/// before cancelling, SAP is asked for the send by its order number. If it is there after all, the
/// transfer is finished into transit instead, and the sender is told to recall it.
/// </remarks>
public sealed class CancelFactoryTransferHandler(
    ApplicationDbContext context,
    IUserManagementService userManagement,
    ISAPServiceLayerClient sapClient,
    IFactoryTransferLegPoster poster,
    IAuditService auditService,
    ILogger<CancelFactoryTransferHandler> logger)
    : IRequestHandler<CancelFactoryTransferCommand, ErrorOr<FactoryTransferActionResultDto>>
{
    public async Task<ErrorOr<FactoryTransferActionResultDto>> Handle(
        CancelFactoryTransferCommand command,
        CancellationToken cancellationToken)
    {
        var actor = await FactoryTransferActor.ResolveAsync(context, userManagement, command.UserId, cancellationToken);
        if (actor is null)
            return Errors.FactoryTransfer.UserNotFound;

        var transfer = await context.FactoryTransfers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == command.TransferId, cancellationToken);
        if (transfer is null || !actor.MaySee(transfer.SourceWarehouseCode, transfer.DestinationWarehouseCode))
            return Errors.FactoryTransfer.NotFound(command.TransferId);

        if (transfer.SentByUserId != actor.UserId)
            return Errors.FactoryTransfer.OnlySenderMayCancel;
        if (transfer.Status != FactoryTransferStatuses.SendFailed)
            return Errors.FactoryTransfer.NotActionable(transfer.Status);
        if (transfer.OutSapDocEntry is not null)
            return Errors.FactoryTransfer.CannotCancelPosted;

        if (transfer.OutAttempts > 0)
        {
            InventoryTransfer? existing;
            try
            {
                existing = await sapClient.FindStockTransferByOrderNumberAsync(
                    transfer.OutOrderNumber, transfer.SentAtUtc.AddDays(-2), cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not check SAP before cancelling factory transfer {Number}", transfer.Number);
                return Errors.FactoryTransfer.PostFailed(
                    "Could not check SAP for this transfer, so it was not cancelled. Try again once SAP is answering.");
            }

            if (existing is not null)
                return await AdoptAsync(transfer, actor);
        }

        var cancelled = await context.FactoryTransfers
            .Where(item => item.Id == transfer.Id && item.Status == FactoryTransferStatuses.SendFailed && item.OutSapDocEntry == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, FactoryTransferStatuses.Cancelled)
                .SetProperty(item => item.Decision, (string?)null)
                .SetProperty(item => item.DecidedByUserId, actor.UserId)
                .SetProperty(item => item.DecidedByName, actor.Name)
                .SetProperty(item => item.DecidedAtUtc, DateTime.UtcNow),
                cancellationToken);
        if (cancelled == 0)
            return Errors.FactoryTransfer.NotActionable("no longer failed");

        try
        {
            await auditService.LogAsync(
                AuditActions.CancelFactoryTransfer, "FactoryTransfer", transfer.Id.ToString(),
                $"{transfer.Number} cancelled by {actor.Name}; it never reached SAP", true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not audit the cancellation of factory transfer {TransferId}", transfer.Id);
        }

        var detail = await FactoryTransferReader.ReadDetailAsync(context, transfer.Id, actor, CancellationToken.None);
        return new FactoryTransferActionResultDto
        {
            Message = $"{transfer.Number} cancelled. It never reached SAP, so nothing moved.",
            Transfer = detail!
        };
    }

    /// <summary>The send is in SAP after all: finish it into transit through the ordinary retry path.</summary>
    private async Task<ErrorOr<FactoryTransferActionResultDto>> AdoptAsync(FactoryTransferEntity transfer, FactoryTransferActor actor)
    {
        await context.FactoryTransfers
            .Where(item => item.Id == transfer.Id && item.Status == FactoryTransferStatuses.SendFailed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, FactoryTransferStatuses.Sending), CancellationToken.None);

        var posted = await poster.PostOutAsync(transfer.Id);
        if (posted.IsError)
            return posted.Errors;

        var detail = await FactoryTransferReader.ReadDetailAsync(context, transfer.Id, actor, CancellationToken.None);
        return new FactoryTransferActionResultDto
        {
            Message = $"{transfer.Number} was not cancelled: SAP had already moved it into {transfer.HoldingWarehouseCode} "
                + $"(SAP transfer #{posted.Value.OutSapDocNum}). Recall it to bring the stock back.",
            Transfer = detail!
        };
    }
}
