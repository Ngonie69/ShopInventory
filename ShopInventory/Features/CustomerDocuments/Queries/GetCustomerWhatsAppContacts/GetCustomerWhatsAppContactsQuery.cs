using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContacts;

/// <summary>The WhatsApp numbers saved on one customer — an account card or a route customer.</summary>
public sealed record GetCustomerWhatsAppContactsQuery(
    string? CardCode,
    int? RouteCustomerId,
    bool IncludeRemoved = false) : IRequest<ErrorOr<List<CustomerWhatsAppContactDto>>>;
