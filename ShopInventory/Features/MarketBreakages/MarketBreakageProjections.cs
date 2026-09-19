using System.Linq.Expressions;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.MarketBreakages;

/// <summary>
/// The one place a breakage report becomes a DTO, as expressions so the reads project in SQL.
/// </summary>
public static class MarketBreakageProjections
{
    public static readonly Expression<Func<MarketBreakageEntity, MarketBreakageSummaryDto>> Summary =
        breakage => new MarketBreakageSummaryDto
        {
            Id = breakage.Id,
            Status = breakage.Status,
            ReportedByName = breakage.ReportedByName,
            VanWarehouseCode = breakage.VanWarehouseCode,
            CardCode = breakage.CardCode,
            CardName = breakage.CardName,
            CapturedAtUtc = breakage.CapturedAtUtc,
            CreatedAtUtc = breakage.CreatedAtUtc,
            LineCount = breakage.Lines.Count,
            TotalReportedQuantity = breakage.Lines.Sum(line => line.ReportedQuantity),
            TotalConfirmedQuantity = breakage.Lines.Any(line => line.ConfirmedQuantity != null)
                ? breakage.Lines.Sum(line => line.ConfirmedQuantity ?? 0m)
                : null,
            SapDocNum = breakage.SapDocNum
        };

    public static readonly Expression<Func<MarketBreakageEntity, MarketBreakageDetailDto>> Detail =
        breakage => new MarketBreakageDetailDto
        {
            Id = breakage.Id,
            ClientRequestId = breakage.ClientRequestId,
            Status = breakage.Status,
            ReportedByUserId = breakage.ReportedByUserId,
            ReportedByName = breakage.ReportedByName,
            VanWarehouseCode = breakage.VanWarehouseCode,
            CardCode = breakage.CardCode,
            CardName = breakage.CardName,
            Remarks = breakage.Remarks,
            CapturedAtUtc = breakage.CapturedAtUtc,
            CreatedAtUtc = breakage.CreatedAtUtc,
            DecidedByName = breakage.DecidedByName,
            DecidedAtUtc = breakage.DecidedAtUtc,
            DecisionRemarks = breakage.DecisionRemarks,
            ReturnsWarehouseCode = breakage.ReturnsWarehouseCode,
            SapDocEntry = breakage.SapDocEntry,
            SapDocNum = breakage.SapDocNum,
            TransferredAtUtc = breakage.TransferredAtUtc,
            LastAttemptedAtUtc = breakage.LastAttemptedAtUtc,
            LastError = breakage.LastError,
            Lines = breakage.Lines
                .OrderBy(line => line.LineNum)
                .Select(line => new MarketBreakageLineDto
                {
                    Id = line.Id,
                    LineNum = line.LineNum,
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemDescription,
                    Reason = line.Reason,
                    ReportedQuantity = line.ReportedQuantity,
                    ConfirmedQuantity = line.ConfirmedQuantity
                })
                .ToList()
        };
}
