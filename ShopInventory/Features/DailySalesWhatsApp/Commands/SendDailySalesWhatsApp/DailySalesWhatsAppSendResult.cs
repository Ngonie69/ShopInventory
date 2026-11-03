namespace ShopInventory.Features.DailySalesWhatsApp.Commands.SendDailySalesWhatsApp;

/// <summary>What one send did.</summary>
/// <remarks><list type="table">
/// <item><term>SentTo</term><description>Numbers WhatsApp accepted the message for.</description></item>
/// <item><term>Unconfirmed</term><description>
/// Numbers whose send WhatsApp did not confirm — no answer in time, or accepted without a message id. They
/// have very likely received it, so they are treated as sent and never sent to again for the day.
/// </description></item>
/// <item><term>Failed</term><description>Numbers WhatsApp refused, with why. A scheduled pass tries them again until 09:45.</description></item>
/// <item><term>Skipped</term><description>True when nothing was attempted; <c>Reason</c> says why.</description></item>
/// <item><term>Message</term><description>The text sent; null when skipped before the report was read.</description></item>
/// </list></remarks>
public sealed record DailySalesWhatsAppSendResult(
    DateTime TradingDate,
    List<string> SentTo,
    List<string> Unconfirmed,
    List<DailySalesWhatsAppSendFailure> Failed,
    bool Skipped,
    string? Reason,
    string? Message);

public sealed record DailySalesWhatsAppSendFailure(string Number, string Reason);
