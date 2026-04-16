using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CostCentres.Queries.GetCostCentreByCode;

public sealed class GetCostCentreByCodeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetCostCentreByCodeHandler> logger
) : IRequestHandler<GetCostCentreByCodeQuery, ErrorOr<CostCentreDto>>
{
    public async Task<ErrorOr<CostCentreDto>> Handle(
        GetCostCentreByCodeQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.CostCentre.SapDisabled;

        try
        {
            var costCentre = await sapClient.GetCostCentreByCodeAsync(request.CenterCode, cancellationToken);

            if (costCentre is null)
                return Errors.CostCentre.NotFound(request.CenterCode);

            return costCentre;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving cost centre {CenterCode}", request.CenterCode);
            return Errors.CostCentre.SapError(ex.Message);
        }
    }
}
