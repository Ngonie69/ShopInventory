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

namespace ShopInventory.Features.FactoryTransfers.Commands.DecideFactoryTransfer;

/// <summary>
/// The receiving side's answer to a transfer waiting in the holding warehouse: accept it, accept part
/// of it, or reject it. Whatever is not accepted goes back to the sender's warehouse in the same SAP
/// document.
/// </summary>
/// <remarks>
/// Only somebody holding the destination warehouse may answer, or the dispatch manager
/// (<c>factory.transfers.accept_any</c>), and never the person who sent it.
/// </remarks>
public sealed class DecideFactoryTransferHandler(
    ApplicationDbContext context,
    IUserManagementService userManagement,
    IFactoryTransferLegPoster poster,
    IAuditService auditService,
    IOptions<SAPSettings> sapSettings,
    ILogger<DecideFactoryTransferHandler> logger)
    : IRequestHandler<DecideFactoryTransferCommand, ErrorOr<FactoryTransferActionResultDto>>
{
    public async Task<ErrorOr<FactoryTransferActionResultDto>> Handle(
        DecideFactoryTransferCommand command,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.FactoryTransfer.SapDisabled;

        var actor = await FactoryTransferActor.ResolveAsync(context, userManagement, command.UserId, cancellationToken);
        if (actor is null)
            return Errors.FactoryTransfer.UserNotFound;
        if (!actor.CanAccept && !actor.CanAcceptAny)
            return Errors.FactoryTransfer.AcceptNotPermitted;

        var transfer = await context.FactoryTransfers
            .AsNoTracking()
            .Include(item => item.Lines)
            .FirstOrDefaultAsync(item => item.Id == command.TransferId, cancellationToken);
        if (transfer is null || !actor.MaySee(transfer.SourceWarehouseCode, transfer.DestinationWarehouseCode))
            return Errors.FactoryTransfer.NotFound(command.TransferId);

        if (transfer.Status != FactoryTransferStatuses.InTransit)
            return Errors.FactoryTransfer.NotActionable(transfer.Status);

        if (transfer.SentByUserId == actor.UserId)
            return Errors.FactoryTransfer.CannotDecideOwnTransfer;
        if (!actor.MayDecide(transfer))
            return Errors.FactoryTransfer.DestinationNotAssigned(transfer.DestinationWarehouseCode);

        var reason = Blank(command.Reason);
        var accepted = new Dictionary<int, decimal>();
        if (command.IsAccept)
        {
            var check = CheckLines(transfer, command.Lines, reason);
            if (check.IsError)
                return check.Errors;
            accepted = command.Lines.ToDictionary(line => line.LineId, line => line.AcceptedQuantity);
        }

        var decision = command.IsAccept ? FactoryTransferDecisions.Accepted : FactoryTransferDecisions.Rejected;
        var claimed = await FactoryTransferSettlement.ClaimAsync(
            context, transfer.Id, decision, actor, reason, accepted, cancellationToken);
        if (!claimed)
        {
            var status = await context.FactoryTransfers.AsNoTracking()
                .Where(item => item.Id == transfer.Id)
                .Select(item => item.Status)
                .FirstAsync(cancellationToken);
            return Errors.FactoryTransfer.NotActionable(status);
        }

        // Past the claim: the decision is recorded, and the post is an obligation.
        var posted = await poster.PostInAsync(transfer.Id);
        await AuditAsync(transfer, decision, actor, posted);

        var detail = await FactoryTransferReader.ReadDetailAsync(context, transfer.Id, actor, CancellationToken.None);
        if (posted.IsError)
            return posted.Errors
                .Select(error => Error.Custom((int)error.Type, error.Code,
                    $"Your decision on {transfer.Number} is saved but SAP has not moved the stock yet. {error.Description}"))
                .ToList();

        var result = posted.Value;
        var message = result.Status switch
        {
            FactoryTransferStatuses.Accepted =>
                $"{transfer.Number} accepted into {transfer.DestinationWarehouseCode} (SAP transfer #{result.InSapDocNum}).",
            FactoryTransferStatuses.PartiallyAccepted =>
                $"{transfer.Number} part-accepted into {transfer.DestinationWarehouseCode}; the rest went back to "
                + $"{transfer.SourceWarehouseCode} (SAP transfer #{result.InSapDocNum}).",
            _ => $"{transfer.Number} rejected; the stock went back to {transfer.SourceWarehouseCode} (SAP transfer #{result.InSapDocNum})."
        };

        return new FactoryTransferActionResultDto { Message = message, Transfer = detail! };
    }

    /// <summary>
    /// Exactly this transfer's lines, none above what was sent, not all zero, and a reason for any
    /// shortfall — a count sent for a transfer that has since changed must not post.
    /// </summary>
    internal static ErrorOr<Success> CheckLines(
        FactoryTransferEntity transfer,
        IReadOnlyList<DecideFactoryTransferLineDto> lines,
        string? reason)
    {
        var sent = transfer.Lines.ToDictionary(line => line.Id, line => line.Quantity);
        if (!sent.Keys.ToHashSet().SetEquals(lines.Select(line => line.LineId)))
            return Errors.FactoryTransfer.LinesMismatch(
                "The accepted lines do not match this transfer's lines. Reload it and count it again.");

        foreach (var line in lines)
        {
            if (line.AcceptedQuantity > sent[line.LineId])
                return Errors.FactoryTransfer.LinesMismatch(
                    $"Line {line.LineId}: {line.AcceptedQuantity} is more than the {sent[line.LineId]} sent.");
        }

        if (lines.All(line => line.AcceptedQuantity <= 0))
            return Errors.FactoryTransfer.NothingAccepted;

        if (lines.Any(line => line.AcceptedQuantity < sent[line.LineId]) && reason is null)
            return Errors.FactoryTransfer.ShortAcceptNeedsReason;

        return Result.Success;
    }

    private async Task AuditAsync(
        FactoryTransferEntity transfer,
        string decision,
        FactoryTransferActor actor,
        ErrorOr<FactoryTransferEntity> posted)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.DecideFactoryTransfer, "FactoryTransfer", transfer.Id.ToString(),
                $"{transfer.Number} {decision.ToLowerInvariant()} by {actor.Name} for {transfer.DestinationWarehouseCode}"
                + (posted.IsError ? $"; not posted: {posted.FirstError.Description}" : $"; {posted.Value.Status}, SAP #{posted.Value.InSapDocNum}"),
                !posted.IsError);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not audit the decision on factory transfer {TransferId}", transfer.Id);
        }
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
