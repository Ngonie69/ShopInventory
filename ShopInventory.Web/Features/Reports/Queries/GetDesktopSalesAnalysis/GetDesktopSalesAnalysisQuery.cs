using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// A period's till takings broken down by payment method, day, hour, shop, business partner, operator
/// and item.
/// </summary>
/// <remarks>
/// The API decides whose shops the signed-in account may read, so the warehouse is a filter and a
/// shop-confined account naming another shop is refused rather than rescoped. The payment method, when
/// given, confines every figure to that tender, and the source system to one channel — "KefalosVending"
/// for the depots.
///
/// <c>Vans</c> asks the van sales analysis instead, in the same shape: the warehouse is then a van, and the
/// source system is ignored. It reads online van sales too, which never become desktop sales.
/// </remarks>
public sealed record GetDesktopSalesAnalysisQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    string? WarehouseCode,
    string? PaymentMethod = null,
    string? SourceSystem = null,
    bool Vans = false
) : IRequest<ErrorOr<DesktopSalesAnalysisResult>>;
