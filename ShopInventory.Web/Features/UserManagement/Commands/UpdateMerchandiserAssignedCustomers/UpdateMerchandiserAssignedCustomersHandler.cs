using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.UserManagement.Commands.UpdateMerchandiserAssignedCustomers;

public sealed class UpdateMerchandiserAssignedCustomersHandler(
    IUserManagementService userManagementService,
    ILogger<UpdateMerchandiserAssignedCustomersHandler> logger
) : IRequestHandler<UpdateMerchandiserAssignedCustomersCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        UpdateMerchandiserAssignedCustomersCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            await userManagementService.UpdateMerchandiserAssignedCustomersAsync(
                request.UserId,
                request.AssignedWarehouseCodes,
                request.AssignedCustomerCodes,
                cancellationToken);

            return request.Username;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to update merchandiser assignments for {Username}", request.Username);
            return Errors.UserManagement.UpdateMerchandiserAssignedCustomersFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error updating merchandiser assignments for {Username}", request.Username);
            return Errors.UserManagement.UpdateMerchandiserAssignedCustomersFailed("Failed to update merchandiser assignments.");
        }
    }
}