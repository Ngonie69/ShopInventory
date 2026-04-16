using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Email.Commands.QueueEmail;

public sealed record QueueEmailCommand(
    SendEmailRequest Request,
    string? Category
) : IRequest<ErrorOr<Success>>;
