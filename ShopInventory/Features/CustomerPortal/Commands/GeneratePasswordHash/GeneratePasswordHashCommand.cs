using ShopInventory.DTOs;
using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CustomerPortal.Commands.GeneratePasswordHash;

public sealed record GeneratePasswordHashCommand(
    string Password
) : IRequest<ErrorOr<PasswordHashResponse>>;
