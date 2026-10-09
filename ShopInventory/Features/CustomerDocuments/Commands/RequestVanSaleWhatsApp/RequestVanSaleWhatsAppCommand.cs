using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Commands.RequestVanSaleWhatsApp;

/// <summary>
/// A van rep asks for the invoice of a sale they just made to go to the number the customer gave.
/// </summary>
public sealed record RequestVanSaleWhatsAppCommand(Guid UserId, string VanOrder, VanSaleWhatsAppRequest Request)
    : IRequest<ErrorOr<VanSaleWhatsAppResponse>>;
