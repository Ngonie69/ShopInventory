using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetOrderFulfillment;

/// <remarks>
/// <c>CardCode</c> scopes the report to one business partner. Null reports on every customer — the
/// form the insights pages ask for; a sales rep always names a partner.
/// </remarks>
public sealed record GetOrderFulfillmentQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    string? CardCode = null
) : IRequest<ErrorOr<OrderFulfillmentReportDto>>;
