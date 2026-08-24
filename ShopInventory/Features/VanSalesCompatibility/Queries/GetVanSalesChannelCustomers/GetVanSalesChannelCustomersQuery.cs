using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesChannelCustomers;

/// <summary>
/// Every customer in one trade channel, company-wide.
/// </summary>
/// <remarks>
/// The channel is the caller's rather than the route's, which is what makes this the one handset
/// customer read that is not scoped to the signed-in rep — see
/// <see cref="ShopInventory.Common.Mobile.ChannelCustomerAccess"/> for who is allowed it.
/// </remarks>
public sealed record GetVanSalesChannelCustomersQuery(
    Guid UserId,
    string Channel
) : IRequest<ErrorOr<List<VanSalesChannelCustomerDto>>>;
