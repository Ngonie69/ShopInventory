using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerProfile;

/// <summary>
/// Who the signed-in customer is, and when the van is next due.
/// </summary>
/// <remarks>
/// The account comes from the caller's token, never from the request. A profile endpoint that took
/// an id would let any signed-in customer read any other shop's details and delivery schedule.
/// </remarks>
public sealed record GetVanSalesCustomerProfileQuery(
    int AccountId
) : IRequest<ErrorOr<VanSalesCustomerProfileResult>>;
