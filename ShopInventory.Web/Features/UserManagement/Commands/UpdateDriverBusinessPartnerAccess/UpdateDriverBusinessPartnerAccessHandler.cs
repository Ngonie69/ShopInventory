using System.Text.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.UserManagement.Commands.UpdateDriverBusinessPartnerAccess;

public sealed class UpdateDriverBusinessPartnerAccessHandler(
    IAppSettingsService appSettingsService,
    IUserManagementService userManagementService,
    ILogger<UpdateDriverBusinessPartnerAccessHandler> logger
) : IRequestHandler<UpdateDriverBusinessPartnerAccessCommand, ErrorOr<int>>
{
    public async Task<ErrorOr<int>> Handle(
        UpdateDriverBusinessPartnerAccessCommand request,
        CancellationToken cancellationToken)
    {
        var assignedCustomerCodes = request.AssignedCustomerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code)
            .ToList();

        try
        {
            var serializedCodes = JsonSerializer.Serialize(assignedCustomerCodes);
            await appSettingsService.SaveSettingAsync(
                SettingKeys.DriverVisibleBusinessPartners,
                serializedCodes,
                request.ModifiedBy);

            return await userManagementService.UpdateAllDriverAssignedCustomersAsync(assignedCustomerCodes, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to update driver business partner access");
            return Errors.UserManagement.UpdateDriverBusinessPartnerAccessFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error updating driver business partner access");
            return Errors.UserManagement.UpdateDriverBusinessPartnerAccessFailed("Failed to update driver business partner access.");
        }
    }
}