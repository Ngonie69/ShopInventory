using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Vending.Queries.ExportVendorPerformance;

/// <summary>
/// The Depots &amp; vendors workbook for a period: by depot, by vendor and by item, against the period
/// before. <paramref name="DepotCode"/> narrows it to one depot; null is every depot.
/// </summary>
public sealed record ExportVendorPerformanceQuery(DateTime From, DateTime To, string? DepotCode)
    : IRequest<ErrorOr<ExportVendorPerformanceResult>>;
