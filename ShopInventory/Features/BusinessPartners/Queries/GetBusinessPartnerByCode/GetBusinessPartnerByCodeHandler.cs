using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnerByCode;

public sealed class GetBusinessPartnerByCodeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetBusinessPartnerByCodeHandler> logger
) : IRequestHandler<GetBusinessPartnerByCodeQuery, ErrorOr<BusinessPartnerDto>>
{
    public async Task<ErrorOr<BusinessPartnerDto>> Handle(
        GetBusinessPartnerByCodeQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.BusinessPartner.SapDisabled;

        try
        {
            var partner = await sapClient.GetBusinessPartnerByCodeAsync(request.CardCode, cancellationToken);

            if (partner is null)
                return Errors.BusinessPartner.NotFound(request.CardCode);

            return partner;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving business partner {CardCode}", request.CardCode);
            return Errors.BusinessPartner.SapError(ex.Message);
        }
    }
}
