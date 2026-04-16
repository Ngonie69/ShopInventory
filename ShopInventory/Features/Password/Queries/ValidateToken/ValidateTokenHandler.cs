using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Password.Queries.ValidateToken;

public sealed class ValidateTokenHandler(
    IPasswordResetService passwordResetService,
    ILogger<ValidateTokenHandler> logger
) : IRequestHandler<ValidateTokenQuery, ErrorOr<bool>>
{
    public async Task<ErrorOr<bool>> Handle(
        ValidateTokenQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await passwordResetService.ValidateTokenAsync(query.Token);

            if (!result.IsSuccess)
                return Errors.Password.InvalidToken;

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating password reset token");
            return Errors.Password.InvalidToken;
        }
    }
}
