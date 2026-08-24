using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomerInvoices;

/// <summary>
/// Every invoice SAP holds against one customer, whoever raised it.
/// </summary>
/// <remarks>
/// Distinct from <c>GetVanSalesCustomerHistoryQuery</c>, which answers the same-sounding question for
/// a shop on the signed-in rep's own route out of this platform's route-customer tables. This one is
/// read against SAP and is not route-scoped, because a channel's customers are mostly on other
/// people's routes and because a General Trade account buys through more than the van.
/// </remarks>
public sealed record GetVanSalesCustomerInvoicesQuery(
    Guid UserId,
    string Code,
    DateTime? From,
    DateTime? To,
    int? Page,
    int? PageSize
) : IRequest<ErrorOr<InvoiceDateResponseDto>>;
