using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DailyIncomingPayments.Queries.GetIncomingPaymentGlMappings;

/// <summary>Every business partner's daily incoming payment G/L accounts.</summary>
public sealed record GetIncomingPaymentGlMappingsQuery : IRequest<ErrorOr<List<IncomingPaymentGlMappingDto>>>;
