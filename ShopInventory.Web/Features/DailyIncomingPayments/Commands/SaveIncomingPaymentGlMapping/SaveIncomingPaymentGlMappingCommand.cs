using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailyIncomingPayments.Commands.SaveIncomingPaymentGlMapping;

/// <summary>Saves a business partner's G/L accounts, run and recipients. The API checks both accounts in SAP.</summary>
public sealed record SaveIncomingPaymentGlMappingCommand(string CardCode, SaveIncomingPaymentGlMappingBody Body) : IRequest<ErrorOr<IncomingPaymentGlMapping>>;
