using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnersByCodes;

public sealed class GetBusinessPartnersByCodesHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetBusinessPartnersByCodesHandler> logger
) : IRequestHandler<GetBusinessPartnersByCodesQuery, ErrorOr<BusinessPartnerListResponseDto>>
{
    public async Task<ErrorOr<BusinessPartnerListResponseDto>> Handle(
        GetBusinessPartnersByCodesQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.BusinessPartner.SapDisabled;

        // An empty request is a valid question with an empty answer — a rep with no assigned
        // customers should get a list, not an error.
        if (request.CardCodes.Count == 0)
        {
            return new BusinessPartnerListResponseDto
            {
                TotalCount = 0,
                BusinessPartners = new List<BusinessPartnerDto>()
            };
        }

        try
        {
            var partners = await sapClient.GetBusinessPartnersByCodesAsync(request.CardCodes, cancellationToken);

            return new BusinessPartnerListResponseDto
            {
                TotalCount = partners.Count,
                BusinessPartners = partners
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving {Count} business partner(s) by code", request.CardCodes.Count);
            return Errors.BusinessPartner.SapError(ex.Message);
        }
    }
}
