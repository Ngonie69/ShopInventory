using ErrorOr;
using MediatR;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanSalesAnalysis;

/// <summary>
/// The desktop sales breakdown, for the vans alone: takings by payment method, day, hour, van, customer,
/// channel, rep and item.
/// </summary>
/// <remarks>
/// Answered in the desktop analysis's own shape so the portal draws both with one page, but read through
/// <see cref="VanSalesFactReader"/> rather than from <c>DesktopSales</c>. An online van sale leaves no
/// desktop sale — only a confirmed reservation — so the desktop analysis confined to vans silently
/// leaves out every sale a van made with signal.
///
/// The dates are inclusive CAT trading days. <c>WarehouseCode</c> confines it to one van;
/// <c>PaymentMethod</c> to one tender by its reporting name, "Not recorded" selecting the sales that
/// named none.
/// </remarks>
public sealed record GetVanSalesAnalysisQuery(
    DateTime FromDate,
    DateTime ToDate,
    string? WarehouseCode = null,
    string? PaymentMethod = null
) : IRequest<ErrorOr<DesktopSalesAnalysisResult>>;
