using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Stock.Queries.GetSalesInWarehouse;

public sealed class GetSalesInWarehouseHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetSalesInWarehouseHandler> logger
) : IRequestHandler<GetSalesInWarehouseQuery, ErrorOr<WarehouseSalesResponseDto>>
{
    public async Task<ErrorOr<WarehouseSalesResponseDto>> Handle(
        GetSalesInWarehouseQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Stock.SapDisabled;

        try
        {
            var salesItems = await sapClient.GetSalesQuantitiesByWarehouseAsync(
                request.WarehouseCode, request.FromDate, request.ToDate, cancellationToken);

            var response = new WarehouseSalesResponseDto
            {
                WarehouseCode = request.WarehouseCode,
                FromDate = request.FromDate,
                ToDate = request.ToDate,
                TotalItemsSold = salesItems.Count,
                TotalSalesValue = salesItems.Sum(s => s.TotalSalesValue),
                TotalInvoices = salesItems.Sum(s => s.InvoiceCount),
                Items = salesItems
            };

            logger.LogInformation("Retrieved {Count} sales items in warehouse {Warehouse} from {From} to {To}",
                response.TotalItemsSold, request.WarehouseCode,
                request.FromDate.ToString("yyyy-MM-dd"), request.ToDate.ToString("yyyy-MM-dd"));

            return response;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Stock.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Stock.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving sales for warehouse {Warehouse}", request.WarehouseCode);
            return Errors.Stock.SapError(ex.Message);
        }
    }
}
