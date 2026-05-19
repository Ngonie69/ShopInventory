using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.WhatsApp.Queries.GetWhatsAppHealth;

public sealed record GetWhatsAppHealthQuery() : IRequest<ErrorOr<WhatsAppHealthDto>>;