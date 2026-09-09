using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetVendorsForAccount;

/// <summary>
/// The vendors the signed-in account may invoice.
/// </summary>
/// <remarks>
/// Takes no business partner. That is the point of it: <c>GET /api/route-customers</c> filters on a
/// code the caller supplies and returns every route's customers when none is given, which is right
/// for an administrator and wrong for a till. Here the code is read off the account, through the same
/// resolver the sale uses, so the list an operator picks from and the set the server will accept
/// cannot drift apart — and a till cannot reach another shop's vendors because it never names one.
/// </remarks>
public sealed record GetVendorsForAccountQuery(Guid UserId) : IRequest<ErrorOr<List<DesktopVendorDto>>>;
