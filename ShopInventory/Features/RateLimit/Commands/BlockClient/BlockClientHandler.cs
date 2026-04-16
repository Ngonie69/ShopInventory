using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Commands.BlockClient;

public sealed class BlockClientHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<BlockClientCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        BlockClientCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await rateLimitService.BlockClientAsync(command.ClientId, command.DurationMinutes, command.Reason, cancellationToken);
            return $"Client '{command.ClientId}' blocked for {command.DurationMinutes} minutes";
        }
        catch (Exception ex)
        {
            return Errors.RateLimit.BlockFailed(ex.Message);
        }
    }
}
