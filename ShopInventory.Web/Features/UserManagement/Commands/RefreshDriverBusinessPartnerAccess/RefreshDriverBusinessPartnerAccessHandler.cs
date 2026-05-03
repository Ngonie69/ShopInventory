using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.UserManagement.Commands.RefreshDriverBusinessPartnerAccess;

public sealed class RefreshDriverBusinessPartnerAccessHandler(
    IMasterDataCacheService masterDataCacheService,
    ILogger<RefreshDriverBusinessPartnerAccessHandler> logger
) : IRequestHandler<RefreshDriverBusinessPartnerAccessCommand, ErrorOr<int>>
{
    public async Task<ErrorOr<int>> Handle(
        RefreshDriverBusinessPartnerAccessCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await masterDataCacheService.SyncBusinessPartnersFromApiAsync();
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to refresh driver business partner access cache from SAP");
            return Errors.UserManagement.RefreshDriverBusinessPartnerAccessFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error refreshing driver business partner access cache from SAP");
            return Errors.UserManagement.RefreshDriverBusinessPartnerAccessFailed("Failed to refresh business partners from SAP.");
        }
    }
}