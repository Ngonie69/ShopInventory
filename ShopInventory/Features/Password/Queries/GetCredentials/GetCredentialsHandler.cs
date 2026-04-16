using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Password.Queries.GetCredentials;

public sealed class GetCredentialsHandler(
    IPasswordResetService passwordResetService,
    ILogger<GetCredentialsHandler> logger
) : IRequestHandler<GetCredentialsQuery, ErrorOr<UpdateCredentialsResponse>>
{
    public async Task<ErrorOr<UpdateCredentialsResponse>> Handle(
        GetCredentialsQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await passwordResetService.GetCredentialsAsync(query.UserId);

            if (result == null)
                return Errors.Password.CredentialsNotFound;

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting credentials for user {UserId}", query.UserId);
            return Errors.Password.UserNotFound(query.UserId.ToString());
        }
    }
}
