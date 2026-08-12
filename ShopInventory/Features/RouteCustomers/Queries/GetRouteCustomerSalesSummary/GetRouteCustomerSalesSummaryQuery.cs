using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSalesSummary;

/// <summary>
/// Every route customer with what they bought in the window, grouped by route.
///
/// Deliberately returns customers who bought nothing as well. They are the report: a route summary
/// answers "who is trading", and the dormancy view is the same rows read from the other end. Filtering
/// them out in the query would make the second report impossible to build from the first.
///
/// <c>DormantDays</c> — days without a sale after which a customer counts as dormant — is echoed back on
/// the response so the caller does not have to re-derive it, and so two pages cannot disagree about what
/// dormant means.
/// </summary>
public sealed record GetRouteCustomerSalesSummaryQuery(
    string? AssignedBusinessPartnerCode,
    DateTime? From,
    DateTime? To,
    int? DormantDays,
    bool IncludeInactive
) : IRequest<ErrorOr<RouteCustomerSalesSummaryDto>>;
