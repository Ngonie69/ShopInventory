using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.UserManagement.Commands.CreateMerchandiserAccount;

public sealed class CreateMerchandiserAccountHandler(
    IUserManagementService userManagementService,
    ILogger<CreateMerchandiserAccountHandler> logger
) : IRequestHandler<CreateMerchandiserAccountCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        CreateMerchandiserAccountCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            await userManagementService.CreateMerchandiserAccountAsync(request.Request, cancellationToken);
            return request.Request.Username;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Merchandiser account creation request failed for {Username}", request.Request.Username);
            return Errors.UserManagement.CreateMerchandiserAccountFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error creating merchandiser account for {Username}", request.Request.Username);
            return Errors.UserManagement.CreateMerchandiserAccountFailed("Failed to create merchandiser account.");
        }
    }
}