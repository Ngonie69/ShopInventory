using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateVendorForAccount;

/// <summary>
/// Adds a vendor to the depot the signed-in cart-vendor account sells on.
/// </summary>
/// <remarks>
/// The write half of <c>GetVendorsForAccountQuery</c>, and scoped the same way: the business partner is
/// resolved off the account through <c>SellingAccountResolver</c>, never taken from the request.
/// </remarks>
public sealed record CreateVendorForAccountCommand(
    CreateDesktopVendorRequest Request,
    Guid UserId) : IRequest<ErrorOr<DesktopVendorDto>>;
