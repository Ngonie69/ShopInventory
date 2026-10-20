using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.FactoryTransfers.Commands.RecallFactoryTransfer;

/// <summary>
/// The sender takes a transfer back before anybody has accepted it: everything in the holding
/// warehouse goes back to the warehouse it came from.
/// </summary>
public sealed class RecallFactoryTransferHandler(
    ApplicationDbContext context,
    IUserManagementService userManagement,
    IFactoryTransferLegPoster poster,
    IAuditService auditService,
    IOptions<SAPSettings> sapSettings,
    ILogger<RecallFactoryTransferHandler> logger)
    : IRequestHandler<RecallFactoryTransferCommand, ErrorOr<FactoryTransferActionResultDto>>
{
    public async Task<ErrorOr<FactoryTransferActionResultDto>> Handle(
        RecallFactoryTransferCommand command,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.FactoryTransfer.SapDisabled;

        var actor = await FactoryTransferActor.ResolveAsync(context, userManagement, command.UserId, cancellationToken);
        if (actor is null)
            return Errors.FactoryTransfer.UserNotFound;

        var transfer = await context.FactoryTransfers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == command.TransferId, cancellationToken);
        if (transfer is null || !actor.MaySee(transfer.SourceWarehouseCode, transfer.DestinationWarehouseCode))
            return Errors.FactoryTransfer.NotFound(command.TransferId);

        if (transfer.SentByUserId != actor.UserId || !actor.CanSend)
            return Errors.FactoryTransfer.OnlySenderMayRecall;
        if (transfer.Status != FactoryTransferStatuses.InTransit)
            return Errors.FactoryTransfer.NotActionable(transfer.Status);

        var claimed = await FactoryTransferSettlement.ClaimAsync(
            context, transfer.Id, FactoryTransferDecisions.Recalled, actor, command.Reason.Trim(),
            new Dictionary<int, decimal>(), cancellationToken);
        if (!claimed)
        {
            var status = await context.FactoryTransfers.AsNoTracking()
                .Where(item => item.Id == transfer.Id)
                .Select(item => item.Status)
                .FirstAsync(cancellationToken);
            return Errors.FactoryTransfer.NotActionable(status);
        }

        var posted = await poster.PostInAsync(transfer.Id);

        try
        {
            await auditService.LogAsync(
                AuditActions.RecallFactoryTransfer, "FactoryTransfer", transfer.Id.ToString(),
                $"{transfer.Number} recalled by {actor.Name} to {transfer.SourceWarehouseCode}"
                + (posted.IsError ? $"; not posted: {posted.FirstError.Description}" : $"; SAP #{posted.Value.InSapDocNum}"),
                !posted.IsError);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not audit the recall of factory transfer {TransferId}", transfer.Id);
        }

        if (posted.IsError)
            return posted.Errors
                .Select(error => Error.Custom((int)error.Type, error.Code,
                    $"The recall of {transfer.Number} is saved but SAP has not moved the stock back yet. {error.Description}"))
                .ToList();

        var detail = await FactoryTransferReader.ReadDetailAsync(context, transfer.Id, actor, CancellationToken.None);
        return new FactoryTransferActionResultDto
        {
            Message = $"{transfer.Number} recalled; the stock went back to {transfer.SourceWarehouseCode} (SAP transfer #{posted.Value.InSapDocNum}).",
            Transfer = detail!
        };
    }
}
