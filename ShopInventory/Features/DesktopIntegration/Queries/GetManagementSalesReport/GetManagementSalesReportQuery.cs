using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

/// <summary>
/// The management sales report: a period's sales against the period before it, broken down by every
/// dimension a decision is made on, with gross margin from SAP and the state of posting and fiscalisation.
/// </summary>
/// <param name="CallerUserId">Whose read scope applies — the same scope as the sales list.</param>
/// <param name="FromDate">First business day, inclusive. Defaults to today.</param>
/// <param name="ToDate">Last business day, inclusive. Defaults to today.</param>
/// <param name="WarehouseCode">One shop or depot's warehouse, or every one the caller may read.</param>
/// <param name="SourceSystem">One channel — till, vending or van — or all of them.</param>
/// <param name="Business">One of <c>SaleBusinesses</c> — shops, vending or vans — or all of them.</param>
public sealed record GetManagementSalesReportQuery(
    Guid CallerUserId,
    DateTime? FromDate,
    DateTime? ToDate,
    string? WarehouseCode = null,
    string? SourceSystem = null,
    string? Business = null
) : IRequest<ErrorOr<ManagementSalesReport>>;
