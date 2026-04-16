using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Email.Commands.SendTestEmail;

public sealed record SendTestEmailCommand(string ToEmail) : IRequest<ErrorOr<EmailSentResponseDto>>;
