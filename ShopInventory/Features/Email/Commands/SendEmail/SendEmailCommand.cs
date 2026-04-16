using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Email.Commands.SendEmail;

public sealed record SendEmailCommand(SendEmailRequest Request) : IRequest<ErrorOr<EmailSentResponseDto>>;
