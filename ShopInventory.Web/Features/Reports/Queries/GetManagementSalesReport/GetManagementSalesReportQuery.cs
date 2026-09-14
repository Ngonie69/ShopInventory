using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport;

/// <summary>
/// The management sales report for a period, set against the period before it.
/// </summary>
/// <remarks>
/// The API decides whose shops the signed-in account may read, as it does for the till analysis, so the
/// warehouse is a filter and naming another shop is refused rather than rescoped.
/// </remarks>
public sealed record GetManagementSalesReportQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    string? WarehouseCode,
    string? SourceSystem
) : IRequest<ErrorOr<ManagementSalesReportResult>>;
