using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Email.Commands.SendEmail;

public sealed class SendEmailHandler(
    IEmailService emailService,
    ILogger<SendEmailHandler> logger
) : IRequestHandler<SendEmailCommand, ErrorOr<EmailSentResponseDto>>
{
    public async Task<ErrorOr<EmailSentResponseDto>> Handle(
        SendEmailCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await emailService.SendEmailAsync(command.Request, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending email");
            return Errors.Email.SendFailed(ex.Message);
        }
    }
}
