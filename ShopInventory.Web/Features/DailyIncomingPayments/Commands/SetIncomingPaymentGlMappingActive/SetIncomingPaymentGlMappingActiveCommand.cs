using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailyIncomingPayments.Commands.SetIncomingPaymentGlMappingActive;

/// <summary>Switches one business partner's daily incoming payment on or off. Its accounts are left as they are.</summary>
public sealed record SetIncomingPaymentGlMappingActiveCommand(string CardCode, bool IsActive) : IRequest<ErrorOr<IncomingPaymentGlMapping>>;
