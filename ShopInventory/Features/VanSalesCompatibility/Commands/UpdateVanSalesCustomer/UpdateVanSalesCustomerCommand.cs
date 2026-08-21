using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.UpdateVanSalesCustomer;

/// <summary>
/// Corrects the contact details a handset holds for a shop on its own route, by the code it knows
/// the shop by.
/// </summary>
public sealed record UpdateVanSalesCustomerCommand(
    Guid UserId,
    string Code,
    VanSalesUpdateCustomerRequest Request) : IRequest<ErrorOr<VanSalesShopDto>>;
