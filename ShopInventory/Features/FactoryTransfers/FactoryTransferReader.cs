using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.FactoryTransfers;

/// <summary>
/// Reads factory transfers for a caller: projected rows, plus what that caller may do to each.
/// </summary>
public static class FactoryTransferReader
{
    /// <summary>The columns a list row needs, read without loading lines.</summary>
    public sealed record Row(
        int Id,
        string DepartmentCode,
        string Direction,
        string SourceWarehouseCode,
        string HoldingWarehouseCode,
        string DestinationWarehouseCode,
        string Reason,
        Guid SentByUserId,
        string SentByName,
        DateTime SentAtUtc,
        string Status,
        string? Decision,
        Guid? DecidedByUserId,
        string? DecidedByName,
        DateTime? DecidedAtUtc,
        int? OutSapDocNum,
        int? InSapDocNum,
        int LineCount);

    public static readonly Expression<Func<FactoryTransferEntity, Row>> RowProjection = transfer => new Row(
        transfer.Id,
        transfer.DepartmentCode,
        transfer.Direction,
        transfer.SourceWarehouseCode,
        transfer.HoldingWarehouseCode,
        transfer.DestinationWarehouseCode,
        transfer.Reason,
        transfer.SentByUserId,
        transfer.SentByName,
        transfer.SentAtUtc,
        transfer.Status,
        transfer.Decision,
        transfer.DecidedByUserId,
        transfer.DecidedByName,
        transfer.DecidedAtUtc,
        transfer.OutSapDocNum,
        transfer.InSapDocNum,
        transfer.Lines.Count);

    /// <summary>The transfers <paramref name="actor"/> may see: their warehouse at either end, or all.</summary>
    public static IQueryable<FactoryTransferEntity> Visible(IQueryable<FactoryTransferEntity> transfers, FactoryTransferActor actor)
    {
        if (actor.CanAcceptAny)
            return transfers;

        var warehouses = actor.Warehouses.ToList();
        return transfers.Where(transfer =>
            warehouses.Contains(transfer.SourceWarehouseCode) || warehouses.Contains(transfer.DestinationWarehouseCode));
    }

    /// <summary>In transit and decidable by <paramref name="actor"/>; the "waiting for me" queue.</summary>
    public static IQueryable<FactoryTransferEntity> AwaitingDecision(IQueryable<FactoryTransferEntity> transfers, FactoryTransferActor actor)
    {
        var inTransit = transfers.Where(transfer =>
            transfer.Status == FactoryTransferStatuses.InTransit && transfer.SentByUserId != actor.UserId);

        if (actor.CanAcceptAny)
            return inTransit;
        if (!actor.CanAccept)
            return inTransit.Where(_ => false);

        var warehouses = actor.Warehouses.ToList();
        return inTransit.Where(transfer => warehouses.Contains(transfer.DestinationWarehouseCode));
    }

    /// <summary>
    /// Units sent per transfer. Summed here rather than in SQL: SQLite, which the tests run on, cannot
    /// sum a decimal column.
    /// </summary>
    public static async Task<Dictionary<int, decimal>> TotalsAsync(
        ApplicationDbContext context,
        IReadOnlyCollection<int> ids,
        CancellationToken cancellationToken)
    {
        var quantities = await context.FactoryTransferLines
            .AsNoTracking()
            .Where(line => ids.Contains(line.TransferId))
            .Select(line => new { line.TransferId, line.Quantity })
            .ToListAsync(cancellationToken);

        return quantities
            .GroupBy(line => line.TransferId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
    }

    public static FactoryTransferSummaryDto ToSummary(Row row, FactoryTransferActor actor, decimal totalQuantity)
    {
        var isSender = row.SentByUserId == actor.UserId;
        var mayDecide = !isSender
            && ((actor.CanAccept && actor.Holds(row.DestinationWarehouseCode)) || actor.CanAcceptAny);

        var canRetry = row.Status switch
        {
            FactoryTransferStatuses.Sending or FactoryTransferStatuses.SendFailed => isSender || actor.CanAcceptAny,
            FactoryTransferStatuses.Settling or FactoryTransferStatuses.SettleFailed =>
                isSender || row.DecidedByUserId == actor.UserId || mayDecide,
            _ => false
        };

        return new FactoryTransferSummaryDto
        {
            Id = row.Id,
            Number = FactoryTransferEntity.FormatNumber(row.Id),
            DepartmentCode = row.DepartmentCode,
            DepartmentName = FactoryDepartments.Find(row.DepartmentCode)?.Name ?? row.DepartmentCode,
            Direction = row.Direction,
            SourceWarehouseCode = row.SourceWarehouseCode,
            HoldingWarehouseCode = row.HoldingWarehouseCode,
            DestinationWarehouseCode = row.DestinationWarehouseCode,
            Reason = row.Reason,
            SentByName = row.SentByName,
            SentAtUtc = row.SentAtUtc,
            Status = row.Status,
            Decision = row.Decision,
            DecidedByName = row.DecidedByName,
            DecidedAtUtc = row.DecidedAtUtc,
            OutSapDocNum = row.OutSapDocNum ?? 0,
            InSapDocNum = row.InSapDocNum ?? 0,
            LineCount = row.LineCount,
            TotalQuantity = totalQuantity,
            CanDecide = row.Status == FactoryTransferStatuses.InTransit && mayDecide,
            CanRecall = row.Status == FactoryTransferStatuses.InTransit && isSender && actor.CanSend,
            CanRetry = canRetry,
            CanCancel = row.Status == FactoryTransferStatuses.SendFailed && isSender
        };
    }

    public static async Task<FactoryTransferDetailDto?> ReadDetailAsync(
        ApplicationDbContext context,
        int id,
        FactoryTransferActor actor,
        CancellationToken cancellationToken)
    {
        var row = await context.FactoryTransfers
            .AsNoTracking()
            .Where(transfer => transfer.Id == id)
            .Select(RowProjection)
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null || !actor.MaySee(row.SourceWarehouseCode, row.DestinationWarehouseCode))
            return null;

        var extra = await context.FactoryTransfers
            .AsNoTracking()
            .Where(transfer => transfer.Id == id)
            .Select(transfer => new
            {
                transfer.DecisionReason,
                transfer.LastError,
                transfer.OutSapDocEntry,
                transfer.InSapDocEntry,
                transfer.OutPostedAtUtc,
                transfer.InPostedAtUtc
            })
            .FirstAsync(cancellationToken);

        var lines = await context.FactoryTransferLines
            .AsNoTracking()
            .Where(line => line.TransferId == id)
            .OrderBy(line => line.LineNum)
            .Select(line => new FactoryTransferLineEntity
            {
                Id = line.Id,
                LineNum = line.LineNum,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                UoMCode = line.UoMCode,
                Quantity = line.Quantity,
                AcceptedQuantity = line.AcceptedQuantity,
                BatchesJson = line.BatchesJson
            })
            .ToListAsync(cancellationToken);

        return new FactoryTransferDetailDto
        {
            Summary = ToSummary(row, actor, lines.Sum(line => line.Quantity)),
            DecisionReason = extra.DecisionReason,
            LastError = extra.LastError,
            OutSapDocEntry = extra.OutSapDocEntry ?? 0,
            InSapDocEntry = extra.InSapDocEntry ?? 0,
            OutPostedAtUtc = extra.OutPostedAtUtc,
            InPostedAtUtc = extra.InPostedAtUtc,
            Lines = lines.Select(line => new FactoryTransferLineDto
            {
                Id = line.Id,
                LineNum = line.LineNum,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                UoMCode = line.UoMCode,
                Quantity = line.Quantity,
                AcceptedQuantity = line.AcceptedQuantity,
                Batches = FactoryTransferLegPoster.ReadBatches(line)
                    .Select(batch => new FactoryTransferBatchDto { BatchNumber = batch.BatchNumber, Quantity = batch.Quantity })
                    .ToList()
            }).ToList()
        };
    }
}
