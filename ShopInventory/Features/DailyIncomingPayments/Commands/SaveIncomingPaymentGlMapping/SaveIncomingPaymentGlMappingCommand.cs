using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DailyIncomingPayments.Commands.SaveIncomingPaymentGlMapping;

/// <summary>
/// Creates or replaces a business partner's daily incoming payment G/L accounts and recipients.
/// </summary>
public sealed record SaveIncomingPaymentGlMappingCommand(
    string CardCode,
    string? CardName,
    string CashAccount,
    string ElectronicAccount,
    string Run,
    string? NotifyEmails,
    bool IsActive,
    string? CallerName
) : IRequest<ErrorOr<IncomingPaymentGlMappingDto>>;
