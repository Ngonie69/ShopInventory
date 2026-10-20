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

namespace ShopInventory.Features.FactoryTransfers.Commands.RetryFactoryTransfer;

/// <summary>
/// Posts again whichever leg failed or was stranded: the send, or the decision already recorded.
/// </summary>
/// <remarks>
/// A retry never decides anything new. It re-posts what the record already says, and the poster looks
/// the leg up in SAP by its order number first, so a post whose reply was lost is adopted rather than
/// made twice.
/// </remarks>
public sealed class RetryFactoryTransferHandler(
    ApplicationDbContext context,
    IUserManagementService userManagement,
    IFactoryTransferLegPoster poster,
    IAuditService auditService,
    IOptions<SAPSettings> sapSettings,
    ILogger<RetryFactoryTransferHandler> logger)
    : IRequestHandler<RetryFactoryTransferCommand, ErrorOr<FactoryTransferActionResultDto>>
{
    public async Task<ErrorOr<FactoryTransferActionResultDto>> Handle(
        RetryFactoryTransferCommand command,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.FactoryTransfer.SapDisabled;

        var actor = await FactoryTransferActor.ResolveAsync(context, userManagement, command.UserId, cancellationToken);
        if (actor is null)
            return Errors.FactoryTransfer.UserNotFound;

        var row = await context.FactoryTransfers
            .AsNoTracking()
            .Where(item => item.Id == command.TransferId)
            .Select(FactoryTransferReader.RowProjection)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null || !actor.MaySee(row.SourceWarehouseCode, row.DestinationWarehouseCode))
            return Errors.FactoryTransfer.NotFound(command.TransferId);

        var summary = FactoryTransferReader.ToSummary(row, actor, null);
        if (!summary.CanRetry)
            return row.Status is FactoryTransferStatuses.Sending or FactoryTransferStatuses.SendFailed
                or FactoryTransferStatuses.Settling or FactoryTransferStatuses.SettleFailed
                ? Errors.FactoryTransfer.RetryNotPermitted
                : Errors.FactoryTransfer.NotActionable(row.Status);

        var isOutLeg = row.Status is FactoryTransferStatuses.Sending or FactoryTransferStatuses.SendFailed;
        var (from, to) = isOutLeg
            ? (FactoryTransferStatuses.SendFailed, FactoryTransferStatuses.Sending)
            : (FactoryTransferStatuses.SettleFailed, FactoryTransferStatuses.Settling);

        var claimed = await context.FactoryTransfers
            .Where(item => item.Id == row.Id && (item.Status == from || item.Status == to))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, to), cancellationToken);
        if (claimed == 0)
        {
            var status = await context.FactoryTransfers.AsNoTracking()
                .Where(item => item.Id == row.Id)
                .Select(item => item.Status)
                .FirstAsync(cancellationToken);
            return Errors.FactoryTransfer.NotActionable(status);
        }

        var posted = isOutLeg
            ? await poster.PostOutAsync(row.Id)
            : await poster.PostInAsync(row.Id);

        var number = FactoryTransferEntity.FormatNumber(row.Id);
        try
        {
            await auditService.LogAsync(
                AuditActions.RetryFactoryTransfer, "FactoryTransfer", row.Id.ToString(),
                $"{number} {(isOutLeg ? "send" : "settlement")} retried by {actor.Name}"
                + (posted.IsError ? $"; not posted: {posted.FirstError.Description}" : $"; now {posted.Value.Status}"),
                !posted.IsError);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not audit the retry of factory transfer {TransferId}", row.Id);
        }

        if (posted.IsError)
            return posted.Errors;

        var detail = await FactoryTransferReader.ReadDetailAsync(context, row.Id, actor, CancellationToken.None);
        return new FactoryTransferActionResultDto
        {
            Message = isOutLeg
                ? $"{number} is now in {row.HoldingWarehouseCode} (SAP transfer #{posted.Value.OutSapDocNum}), waiting for {row.DestinationWarehouseCode}."
                : $"{number} is now {posted.Value.Status} (SAP transfer #{posted.Value.InSapDocNum}).",
            Transfer = detail!
        };
    }
}
