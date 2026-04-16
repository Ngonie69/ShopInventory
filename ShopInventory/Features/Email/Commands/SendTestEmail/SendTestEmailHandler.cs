using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Email.Commands.SendTestEmail;

public sealed class SendTestEmailHandler(
    IEmailService emailService,
    ILogger<SendTestEmailHandler> logger
) : IRequestHandler<SendTestEmailCommand, ErrorOr<EmailSentResponseDto>>
{
    public async Task<ErrorOr<EmailSentResponseDto>> Handle(
        SendTestEmailCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await emailService.TestEmailConfigurationAsync(command.ToEmail, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending test email to {ToEmail}", command.ToEmail);
            return Errors.Email.SendFailed(ex.Message);
        }
    }
}
