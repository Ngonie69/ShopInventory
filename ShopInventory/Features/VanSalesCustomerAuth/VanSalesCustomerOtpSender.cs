using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <inheritdoc />
public sealed class VanSalesCustomerOtpSender(
    IOpenWAClient openWaClient,
    IOptions<OpenWASettings> openWaSettings,
    IOptions<VanSalesCustomerAuthSettings> authSettings,
    IHostEnvironment environment,
    ILogger<VanSalesCustomerOtpSender> logger) : IVanSalesCustomerOtpSender
{
    private readonly OpenWASettings _openWa = openWaSettings.Value;
    private readonly VanSalesCustomerAuthSettings _settings = authSettings.Value;

    public async Task<OtpDeliveryChannel> SendAsync(
        string phoneE164,
        string code,
        CancellationToken cancellationToken)
    {
        var masked = VanSalesCustomerPhone.Mask(phoneE164);

        if (_openWa.Enabled && !string.IsNullOrWhiteSpace(_settings.OtpWhatsAppSessionId))
        {
            try
            {
                var request = new WhatsAppSendTextRequestDto
                {
                    ChatId = ToWhatsAppChatId(phoneE164),
                    Text = BuildMessage(code)
                };

                await openWaClient.SendTextAsync(
                    _settings.OtpWhatsAppSessionId,
                    request,
                    cancellationToken);

                logger.LogInformation("Sent a van sales customer sign-in code to {MaskedPhone} over WhatsApp.", masked);
                return OtpDeliveryChannel.WhatsApp;
            }
            catch (Exception ex)
            {
                // Swallowed on purpose: the caller must answer the same either way, and a customer
                // whose code did not arrive can simply ask for another. Logged at warning because a
                // gateway that is down stops every new sign-in and somebody should see it.
                logger.LogWarning(
                    ex,
                    "WhatsApp delivery of a van sales customer sign-in code to {MaskedPhone} failed.",
                    masked);
            }
        }

        if (environment.IsDevelopment() && _settings.LogOtpCodesInDevelopment)
        {
            // Both conditions required. See VanSalesCustomerAuthSettings.LogOtpCodesInDevelopment.
            logger.LogWarning(
                "DEVELOPMENT ONLY: van sales customer sign-in code for {MaskedPhone} is {Code}.",
                masked,
                code);
            return OtpDeliveryChannel.DevelopmentLog;
        }

        logger.LogWarning(
            "No channel was available to deliver a van sales customer sign-in code to {MaskedPhone}.",
            masked);
        return OtpDeliveryChannel.None;
    }

    private string BuildMessage(string code) =>
        _settings.OtpMessageTemplate
            .Replace("{code}", code, StringComparison.OrdinalIgnoreCase)
            .Replace("{minutes}", _settings.OtpTtlMinutes.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// WhatsApp addresses a person as <c>&lt;country&gt;&lt;number&gt;@c.us</c> — digits only, no
    /// leading '+'.
    /// </summary>
    private static string ToWhatsAppChatId(string phoneE164) =>
        new string(phoneE164.Where(char.IsDigit).ToArray()) + "@c.us";
}
