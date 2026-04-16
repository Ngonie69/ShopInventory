using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CostCentres.Queries.GetCostCentresByDimension;

public sealed class GetCostCentresByDimensionHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetCostCentresByDimensionHandler> logger
) : IRequestHandler<GetCostCentresByDimensionQuery, ErrorOr<CostCentreListResponseDto>>
{
    public async Task<ErrorOr<CostCentreListResponseDto>> Handle(
        GetCostCentresByDimensionQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.CostCentre.SapDisabled;

        if (request.Dimension < 1 || request.Dimension > 5)
            return Errors.CostCentre.InvalidDimension;

        try
        {
            var costCentres = await sapClient.GetCostCentresByDimensionAsync(request.Dimension, cancellationToken);

            return new CostCentreListResponseDto
            {
                TotalCount = costCentres.Count,
                CostCentres = costCentres
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving cost centres by dimension {Dimension}", request.Dimension);
            return Errors.CostCentre.SapError(ex.Message);
        }
    }
}
