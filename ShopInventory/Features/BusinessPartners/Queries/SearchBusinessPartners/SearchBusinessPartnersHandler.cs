using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.BusinessPartners.Queries.SearchBusinessPartners;

public sealed class SearchBusinessPartnersHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<SearchBusinessPartnersHandler> logger
) : IRequestHandler<SearchBusinessPartnersQuery, ErrorOr<BusinessPartnerListResponseDto>>
{
    public async Task<ErrorOr<BusinessPartnerListResponseDto>> Handle(
        SearchBusinessPartnersQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.BusinessPartner.SapDisabled;

        if (string.IsNullOrWhiteSpace(request.SearchTerm))
            return Errors.BusinessPartner.SearchTermRequired;

        try
        {
            var partners = await sapClient.SearchBusinessPartnersAsync(request.SearchTerm, cancellationToken);

            return new BusinessPartnerListResponseDto
            {
                TotalCount = partners.Count,
                BusinessPartners = partners
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error searching business partners with term {SearchTerm}", request.SearchTerm);
            return Errors.BusinessPartner.SapError(ex.Message);
        }
    }
}
