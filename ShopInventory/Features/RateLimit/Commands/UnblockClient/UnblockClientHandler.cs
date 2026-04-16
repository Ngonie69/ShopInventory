using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Commands.UnblockClient;

public sealed class UnblockClientHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<UnblockClientCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        UnblockClientCommand command,
        CancellationToken cancellationToken)
    {
        var success = await rateLimitService.UnblockClientAsync(command.ClientId, cancellationToken);
        if (!success)
            return Errors.RateLimit.ClientNotFound(command.ClientId);

        return $"Client '{command.ClientId}' unblocked successfully";
    }
}
