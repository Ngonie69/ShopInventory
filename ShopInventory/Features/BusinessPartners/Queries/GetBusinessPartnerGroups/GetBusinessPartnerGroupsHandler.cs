using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnerGroups;

/// <summary>
/// SAP's business partner groups, so the group code already cached against every partner can be
/// shown as a name rather than as the bare number it is.
/// </summary>
public sealed class GetBusinessPartnerGroupsHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetBusinessPartnerGroupsHandler> logger
) : IRequestHandler<GetBusinessPartnerGroupsQuery, ErrorOr<BusinessPartnerGroupsListResponseDto>>
{
    public async Task<ErrorOr<BusinessPartnerGroupsListResponseDto>> Handle(
        GetBusinessPartnerGroupsQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.BusinessPartner.SapDisabled;

        try
        {
            var groups = await sapClient.GetBusinessPartnerGroupsAsync(cancellationToken);

            logger.LogInformation("Retrieved {Count} business partner groups from SAP", groups.Count);

            return new BusinessPartnerGroupsListResponseDto
            {
                Count = groups.Count,
                Groups = groups
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading business partner groups from SAP");
            return Errors.BusinessPartner.SapError(ex.Message);
        }
    }
}
