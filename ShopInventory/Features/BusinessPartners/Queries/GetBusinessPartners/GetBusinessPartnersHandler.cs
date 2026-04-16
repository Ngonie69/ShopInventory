using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartners;

public sealed class GetBusinessPartnersHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetBusinessPartnersHandler> logger
) : IRequestHandler<GetBusinessPartnersQuery, ErrorOr<BusinessPartnerListResponseDto>>
{
    public async Task<ErrorOr<BusinessPartnerListResponseDto>> Handle(
        GetBusinessPartnersQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.BusinessPartner.SapDisabled;

        try
        {
            var partners = await sapClient.GetBusinessPartnersAsync(cancellationToken);

            return new BusinessPartnerListResponseDto
            {
                TotalCount = partners.Count,
                BusinessPartners = partners
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving business partners");
            return Errors.BusinessPartner.SapError(ex.Message);
        }
    }
}
