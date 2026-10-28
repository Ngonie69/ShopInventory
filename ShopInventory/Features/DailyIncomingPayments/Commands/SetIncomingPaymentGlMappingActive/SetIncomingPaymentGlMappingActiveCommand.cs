using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DailyIncomingPayments.Commands.SetIncomingPaymentGlMappingActive;

/// <summary>
/// Switches one business partner's daily incoming payment on or off, leaving its accounts as they are.
/// </summary>
public sealed record SetIncomingPaymentGlMappingActiveCommand(
    string CardCode,
    bool IsActive,
    string? CallerName
) : IRequest<ErrorOr<IncomingPaymentGlMappingDto>>;

/// <summary>The body of <c>PUT api/daily-incoming-payments/gl-mappings/{cardCode}/active</c>.</summary>
public sealed record SetIncomingPaymentGlMappingActiveRequest(bool IsActive);
