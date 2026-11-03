using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DailySalesWhatsApp.Commands.SendDailySalesWhatsApp;

/// <summary>Sends the daily sales report to WhatsApp numbers, one message each.</summary>
/// <param name="Scheduled">
/// True from the job: yesterday only, only when switched on, and only to the saved numbers that do not
/// have it yet. False from a person: the day and numbers they name, sent now even if sent before.
/// </param>
/// <param name="TradingDate">The day to report; yesterday when null. Ignored on a scheduled send.</param>
/// <param name="Recipients">Numbers to send to instead of the saved ones; ignored on a scheduled send.</param>
/// <param name="CallerName">Who sent it, for the audit trail.</param>
public sealed record SendDailySalesWhatsAppCommand(
    bool Scheduled,
    DateTime? TradingDate = null,
    List<string>? Recipients = null,
    string? CallerName = null
) : IRequest<ErrorOr<DailySalesWhatsAppSendResult>>;
