using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.BusinessPartners.Queries.GetPaymentTerms;

public sealed class GetPaymentTermsHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPaymentTermsHandler> logger
) : IRequestHandler<GetPaymentTermsQuery, ErrorOr<PaymentTermsDto>>
{
    public async Task<ErrorOr<PaymentTermsDto>> Handle(
        GetPaymentTermsQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.BusinessPartner.SapDisabled;

        try
        {
            var terms = await sapClient.GetPaymentTermsByCodeAsync(request.GroupNumber, cancellationToken);

            if (terms is null)
                return Errors.BusinessPartner.PaymentTermsNotFound(request.GroupNumber);

            return terms;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving payment terms {GroupNumber}", request.GroupNumber);
            return Errors.BusinessPartner.SapError(ex.Message);
        }
    }
}
