using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Commands.ResetClient;

public sealed class ResetClientHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<ResetClientCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        ResetClientCommand command,
        CancellationToken cancellationToken)
    {
        var success = await rateLimitService.ResetClientAsync(command.ClientId, cancellationToken);
        if (!success)
            return Errors.RateLimit.ClientNotFound(command.ClientId);

        return $"Rate limit counters for client '{command.ClientId}' reset successfully";
    }
}
