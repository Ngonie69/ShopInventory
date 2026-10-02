using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.MarketBreakages.Queries.ExportMarketBreakages;

public sealed class ExportMarketBreakagesHandler(ApplicationDbContext context)
    : IRequestHandler<ExportMarketBreakagesQuery, ErrorOr<MarketBreakageExportDto>>
{
    /// <summary>
    /// The most reports one export carries. Far above what the office handles between exports; past it
    /// the file says it holds only the newest, rather than the request running unbounded.
    /// </summary>
    public const int MaxReports = 2000;

    public async Task<ErrorOr<MarketBreakageExportDto>> Handle(
        ExportMarketBreakagesQuery query,
        CancellationToken cancellationToken)
    {
        var breakages = MarketBreakageFilters.Status(
            MarketBreakageFilters.Search(context.MarketBreakages.AsNoTracking(), query.Search),
            query.Status);

        var totalCount = await breakages.CountAsync(cancellationToken);
        var reports = await MarketBreakageFilters.Newest(breakages)
            .Take(MaxReports)
            .Select(MarketBreakageProjections.Detail)
            .ToListAsync(cancellationToken);

        return new MarketBreakageExportDto
        {
            Status = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim(),
            Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            GeneratedAtUtc = DateTime.UtcNow,
            TotalCount = totalCount,
            Truncated = totalCount > reports.Count,
            Totals = Totals(reports),
            ByVan = ByVan(reports),
            ByProduct = ByProduct(reports),
            Reports = reports
        };
    }

    private static MarketBreakageExportTotalsDto Totals(List<MarketBreakageDetailDto> reports) => new()
    {
        Reports = reports.Count,
        OpenReports = reports.Count(report => report.Status is MarketBreakageStatuses.Pending
            or MarketBreakageStatuses.TransferFailed or MarketBreakageStatuses.Transferring),
        Lines = reports.Sum(report => report.Lines.Count),
        ReportedQuantity = reports.Sum(Reported),
        CountedQuantity = reports.Sum(Counted),
        TransferredQuantity = reports.Sum(Transferred),
        RejectedQuantity = reports.Where(report => report.Status == MarketBreakageStatuses.Rejected).Sum(Reported)
    };

    private static List<MarketBreakageExportGroupDto> ByVan(List<MarketBreakageDetailDto> reports)
        => reports
            .GroupBy(report => report.VanWarehouseCode, StringComparer.OrdinalIgnoreCase)
            .Select(van => new MarketBreakageExportGroupDto
            {
                Code = van.First().VanWarehouseCode,
                Name = string.Join(", ", van.Select(report => report.ReportedByName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)),
                Reports = van.Count(),
                ReportedQuantity = van.Sum(Reported),
                CountedQuantity = van.Sum(Counted),
                TransferredQuantity = van.Sum(Transferred)
            })
            .OrderByDescending(group => group.ReportedQuantity)
            .ThenBy(group => group.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<MarketBreakageExportGroupDto> ByProduct(List<MarketBreakageDetailDto> reports)
        => reports
            .SelectMany(report => report.Lines.Select(line => (Report: report, Line: line)))
            .GroupBy(entry => entry.Line.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(item => new MarketBreakageExportGroupDto
            {
                Code = item.First().Line.ItemCode,
                Name = item.Select(entry => entry.Line.ItemDescription).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                Reports = item.Select(entry => entry.Report.Id).Distinct().Count(),
                ReportedQuantity = item.Sum(entry => entry.Line.ReportedQuantity),
                CountedQuantity = item.Sum(entry => entry.Line.ConfirmedQuantity ?? 0m),
                TransferredQuantity = item
                    .Where(entry => entry.Report.Status == MarketBreakageStatuses.Transferred)
                    .Sum(entry => entry.Line.ConfirmedQuantity ?? 0m)
            })
            .OrderByDescending(group => group.ReportedQuantity)
            .ThenBy(group => group.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static decimal Reported(MarketBreakageDetailDto report) => report.Lines.Sum(line => line.ReportedQuantity);

    private static decimal Counted(MarketBreakageDetailDto report) => report.Lines.Sum(line => line.ConfirmedQuantity ?? 0m);

    private static decimal Transferred(MarketBreakageDetailDto report)
        => report.Status == MarketBreakageStatuses.Transferred ? Counted(report) : 0m;
}
