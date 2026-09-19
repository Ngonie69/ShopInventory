using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailyIncomingPayments.Queries.GetIncomingPaymentGlMappings;

/// <summary>Every business partner's daily payment G/L accounts.</summary>
public sealed record GetIncomingPaymentGlMappingsQuery : IRequest<ErrorOr<List<IncomingPaymentGlMapping>>>;
