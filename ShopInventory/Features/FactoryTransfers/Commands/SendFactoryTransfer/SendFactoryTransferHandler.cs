using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Validation;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.FactoryTransfers.Commands.SendFactoryTransfer;

/// <summary>
/// Sends stock from one of the caller's warehouses to the other side of their department: saves the
/// transfer, then posts it into the holding warehouse in SAP.
/// </summary>
/// <remarks>
/// <para>
/// The destination is not the caller's to choose freely. A production warehouse sends only to its
/// department's dispatch warehouse, and a dispatch warehouse only to one of its department's
/// production warehouses, so yoghurt stock can never be released into cheese dispatch.
/// </para>
/// <para>
/// Once the record is saved the request's token is dropped: from there the post is an obligation,
/// and a closed tab must not leave the transfer stranded. A failed post leaves the transfer in
/// SendFailed, where the sender can retry or cancel it.
/// </para>
/// </remarks>
public sealed class SendFactoryTransferHandler(
    ApplicationDbContext context,
    IUserManagementService userManagement,
    IFactoryTransferLegPoster poster,
    IAuditService auditService,
    IOptions<SAPSettings> sapSettings,
    ILogger<SendFactoryTransferHandler> logger)
    : IRequestHandler<SendFactoryTransferCommand, ErrorOr<FactoryTransferActionResultDto>>
{
    public async Task<ErrorOr<FactoryTransferActionResultDto>> Handle(
        SendFactoryTransferCommand command,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.FactoryTransfer.SapDisabled;

        var actor = await FactoryTransferActor.ResolveAsync(context, userManagement, command.UserId, cancellationToken);
        if (actor is null)
            return Errors.FactoryTransfer.UserNotFound;
        if (!actor.CanSend)
            return Errors.FactoryTransfer.SendNotPermitted;

        var clientRequestId = command.ClientRequestId.Trim();
        var existing = await FindExistingAsync(clientRequestId, cancellationToken);
        if (existing is not null)
            return await ReplayAsync(existing, actor, cancellationToken);

        var route = ResolveRoute(command.SourceWarehouseCode, command.DestinationWarehouseCode, actor);
        if (route.IsError)
            return route.Errors;
        var (department, direction, source, destination) = route.Value;

        var lines = command.Lines
            .Select((line, index) => new FactoryTransferLineEntity
            {
                LineNum = index,
                ItemCode = line.ItemCode.Trim().ToUpperInvariant(),
                ItemDescription = Blank(line.ItemDescription),
                UoMCode = Blank(line.UoMCode),
                Quantity = line.Quantity
            })
            .ToList();

        var quantityErrors = await UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
            context,
            lines,
            line => line.ItemCode,
            line => line.Quantity,
            line => line.UoMCode,
            (line, uomCode) => line.UoMCode = uomCode,
            cancellationToken);
        if (quantityErrors.Count > 0)
            return Errors.FactoryTransfer.LinesMismatch(string.Join("; ", quantityErrors));

        var transfer = new FactoryTransferEntity
        {
            ClientRequestId = clientRequestId,
            DepartmentCode = department.Code,
            Direction = direction,
            SourceWarehouseCode = source,
            HoldingWarehouseCode = FactoryDepartments.HoldingWarehouseCode,
            DestinationWarehouseCode = destination,
            Reason = command.Reason.Trim(),
            SentByUserId = actor.UserId,
            SentByName = actor.Name,
            SentAtUtc = DateTime.UtcNow,
            Status = FactoryTransferStatuses.Sending,
            Lines = lines
        };

        // The last point at which giving up is free: nothing has been saved or sent.
        cancellationToken.ThrowIfCancellationRequested();

        context.FactoryTransfers.Add(transfer);
        try
        {
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException exception)
        {
            // Two copies of the same submit raced past the lookup; the unique index let one through.
            context.Entry(transfer).State = EntityState.Detached;
            foreach (var line in transfer.Lines)
                context.Entry(line).State = EntityState.Detached;

            var winner = await FindExistingAsync(clientRequestId, CancellationToken.None);
            if (winner is null)
                throw;

            logger.LogInformation(exception, "Factory transfer {ClientRequestId} was saved by a concurrent submit; replaying it", clientRequestId);
            return await ReplayAsync(winner, actor, CancellationToken.None);
        }

        var posted = await poster.PostOutAsync(transfer.Id);
        await AuditAsync(transfer, posted);

        if (posted.IsError)
            return posted.Errors
                .Select(error => Error.Custom((int)error.Type, error.Code, $"{transfer.Number} was saved but not sent. {error.Description}"))
                .ToList();

        var detail = await FactoryTransferReader.ReadDetailAsync(context, transfer.Id, actor, CancellationToken.None);
        return new FactoryTransferActionResultDto
        {
            Message = $"{transfer.Number} sent. SAP transfer #{posted.Value.OutSapDocNum} moved it from {source} into "
                + $"{transfer.HoldingWarehouseCode}; it waits there for {destination} to accept it.",
            Transfer = detail!
        };
    }

    /// <summary>
    /// The department, direction and canonical warehouse codes for a send, or why it is refused.
    /// </summary>
    internal static ErrorOr<(FactoryDepartment Department, string Direction, string Source, string Destination)> ResolveRoute(
        string sourceWarehouseCode,
        string? destinationWarehouseCode,
        FactoryTransferActor actor)
    {
        var located = FactoryDepartments.Locate(sourceWarehouseCode);
        if (located is null)
            return Errors.FactoryTransfer.NotAFactoryWarehouse(sourceWarehouseCode.Trim());

        var (department, side) = located.Value;
        var source = department.Canonical(sourceWarehouseCode)!;
        if (!actor.Holds(source))
            return Errors.FactoryTransfer.SourceNotAssigned(source);

        var allowed = side == FactorySide.Production
            ? new[] { department.DispatchWarehouse }
            : department.ProductionWarehouses.ToArray();

        string? destination;
        if (string.IsNullOrWhiteSpace(destinationWarehouseCode))
        {
            // Only a single possible destination may be left for the server to fill in.
            destination = allowed.Length == 1 ? allowed[0] : null;
            if (destination is null)
                return Errors.FactoryTransfer.InvalidDestination("(none chosen)", department.Name);
        }
        else
        {
            destination = allowed.FirstOrDefault(code =>
                string.Equals(code, destinationWarehouseCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (destination is null)
                return Errors.FactoryTransfer.InvalidDestination(destinationWarehouseCode.Trim(), department.Name);
        }

        var direction = side == FactorySide.Production ? FactoryTransferDirections.Release : FactoryTransferDirections.Return;
        return (department, direction, source, destination);
    }

    private Task<FactoryTransferEntity?> FindExistingAsync(string clientRequestId, CancellationToken cancellationToken)
        => context.FactoryTransfers
            .AsNoTracking()
            .FirstOrDefaultAsync(transfer => transfer.ClientRequestId == clientRequestId, cancellationToken);

    private async Task<ErrorOr<FactoryTransferActionResultDto>> ReplayAsync(
        FactoryTransferEntity existing,
        FactoryTransferActor actor,
        CancellationToken cancellationToken)
    {
        if (existing.SentByUserId != actor.UserId)
            return Errors.FactoryTransfer.DuplicateRequestFromAnotherUser;

        var detail = await FactoryTransferReader.ReadDetailAsync(context, existing.Id, actor, cancellationToken);
        return new FactoryTransferActionResultDto
        {
            Message = $"{existing.Number} was already sent; it is {existing.Status}.",
            Transfer = detail!
        };
    }

    private async Task AuditAsync(FactoryTransferEntity transfer, ErrorOr<FactoryTransferEntity> posted)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.SendFactoryTransfer, "FactoryTransfer", transfer.Id.ToString(),
                $"{transfer.Number} sent by {transfer.SentByName}: {transfer.SourceWarehouseCode} -> "
                + $"{transfer.HoldingWarehouseCode} for {transfer.DestinationWarehouseCode}, {transfer.Lines.Count} line(s)"
                + (posted.IsError ? $"; not posted: {posted.FirstError.Description}" : $"; SAP #{posted.Value.OutSapDocNum}"),
                !posted.IsError);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not audit factory transfer {TransferId}", transfer.Id);
        }
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
