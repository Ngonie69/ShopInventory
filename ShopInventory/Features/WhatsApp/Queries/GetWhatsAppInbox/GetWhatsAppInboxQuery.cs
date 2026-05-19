using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.WhatsApp.Queries.GetWhatsAppInbox;

public sealed record GetWhatsAppInboxQuery(int Page = 1, int PageSize = 50, string? Search = null) : IRequest<ErrorOr<WhatsAppInboxResponseDto>>;