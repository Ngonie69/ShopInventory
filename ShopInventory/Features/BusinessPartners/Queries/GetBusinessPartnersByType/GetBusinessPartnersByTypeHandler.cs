using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnersByType;

public sealed class GetBusinessPartnersByTypeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetBusinessPartnersByTypeHandler> logger
) : IRequestHandler<GetBusinessPartnersByTypeQuery, ErrorOr<BusinessPartnerListResponseDto>>
{
    public async Task<ErrorOr<BusinessPartnerListResponseDto>> Handle(
        GetBusinessPartnersByTypeQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.BusinessPartner.SapDisabled;

        try
        {
            var partners = await sapClient.GetBusinessPartnersByTypeAsync(request.CardType, cancellationToken);

            return new BusinessPartnerListResponseDto
            {
                TotalCount = partners.Count,
                BusinessPartners = partners
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving business partners by type {CardType}", request.CardType);
            return Errors.BusinessPartner.SapError(ex.Message);
        }
    }
}
