using ErrorOr;
using MediatR;

namespace ShopInventory.Features.WhatsApp.Queries.GetWhatsAppRawPayload;

/// <summary>One inbox row's webhook body, exactly as OpenWA posted it.</summary>
public sealed record GetWhatsAppRawPayloadQuery(int Id) : IRequest<ErrorOr<string>>;
