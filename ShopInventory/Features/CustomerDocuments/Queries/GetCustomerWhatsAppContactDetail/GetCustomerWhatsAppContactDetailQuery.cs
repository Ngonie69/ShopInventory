using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContactDetail;

/// <summary>One saved WhatsApp number with who added it, its recent sends and its history.</summary>
public sealed record GetCustomerWhatsAppContactDetailQuery(int ContactId)
    : IRequest<ErrorOr<CustomerWhatsAppContactDetailDto>>;
