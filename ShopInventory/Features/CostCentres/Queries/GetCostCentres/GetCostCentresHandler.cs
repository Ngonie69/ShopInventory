using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CostCentres.Queries.GetCostCentres;

public sealed class GetCostCentresHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetCostCentresHandler> logger
) : IRequestHandler<GetCostCentresQuery, ErrorOr<CostCentreListResponseDto>>
{
    public async Task<ErrorOr<CostCentreListResponseDto>> Handle(
        GetCostCentresQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.CostCentre.SapDisabled;

        try
        {
            var costCentres = await sapClient.GetCostCentresAsync(cancellationToken);

            return new CostCentreListResponseDto
            {
                TotalCount = costCentres.Count,
                CostCentres = costCentres
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving cost centres");
            return Errors.CostCentre.SapError(ex.Message);
        }
    }
}
