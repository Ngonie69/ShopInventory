using System.Linq.Expressions;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.StockWriteOffs;

/// <summary>
/// The one place a write-off becomes a DTO, as expressions so the reads project in SQL.
/// </summary>
public static class StockWriteOffProjections
{
    public static readonly Expression<Func<StockWriteOffEntity, StockWriteOffSummaryDto>> Summary =
        writeOff => new StockWriteOffSummaryDto
        {
            Id = writeOff.Id,
            Status = writeOff.Status,
            WarehouseCode = writeOff.WarehouseCode,
            Reason = writeOff.Reason,
            RaisedByName = writeOff.RaisedByName,
            CreatedAtUtc = writeOff.CreatedAtUtc,
            LineCount = writeOff.Lines.Count,
            TotalQuantity = writeOff.Lines.Sum(line => line.Quantity),
            SapDocNum = writeOff.SapDocNum
        };

    public static readonly Expression<Func<StockWriteOffEntity, StockWriteOffDetailDto>> Detail =
        writeOff => new StockWriteOffDetailDto
        {
            Id = writeOff.Id,
            ClientRequestId = writeOff.ClientRequestId,
            Status = writeOff.Status,
            RaisedByUserId = writeOff.RaisedByUserId,
            RaisedByName = writeOff.RaisedByName,
            WarehouseCode = writeOff.WarehouseCode,
            Reason = writeOff.Reason,
            Remarks = writeOff.Remarks,
            CreatedAtUtc = writeOff.CreatedAtUtc,
            SapDocEntry = writeOff.SapDocEntry,
            SapDocNum = writeOff.SapDocNum,
            PostedAtUtc = writeOff.PostedAtUtc,
            LastAttemptedAtUtc = writeOff.LastAttemptedAtUtc,
            LastError = writeOff.LastError,
            Lines = writeOff.Lines
                .OrderBy(line => line.LineNum)
                .Select(line => new StockWriteOffLineDto
                {
                    Id = line.Id,
                    LineNum = line.LineNum,
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemDescription,
                    Quantity = line.Quantity,
                    UoMCode = line.UoMCode,
                    BatchNumber = line.BatchNumber,
                    SerialNumber = line.SerialNumber
                })
                .ToList()
        };
}
