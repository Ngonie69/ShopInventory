using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Auth.Commands.BeginPasskeyLogin;

public sealed record BeginPasskeyLoginCommand(string Origin, string RpId) : IRequest<ErrorOr<PasskeyAssertionOptionsResponse>>;