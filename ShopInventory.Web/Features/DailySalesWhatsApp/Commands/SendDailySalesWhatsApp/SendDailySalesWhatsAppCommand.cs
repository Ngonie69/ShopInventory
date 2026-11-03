using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailySalesWhatsApp.Commands.SendDailySalesWhatsApp;

/// <summary>Sends the daily sales report now: for the day given (yesterday when null), to the numbers given or the saved ones.</summary>
public sealed record SendDailySalesWhatsAppCommand(DateTime? TradingDate, List<string>? Recipients)
    : IRequest<ErrorOr<DailySalesWhatsAppSendResult>>;
