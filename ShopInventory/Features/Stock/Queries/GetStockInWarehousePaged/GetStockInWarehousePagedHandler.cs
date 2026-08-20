using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Stock.Queries.GetStockInWarehousePaged;

public sealed class GetStockInWarehousePagedHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetStockInWarehousePagedHandler> logger
) : IRequestHandler<GetStockInWarehousePagedQuery, ErrorOr<WarehouseStockPagedResponseDto>>
{
    public async Task<ErrorOr<WarehouseStockPagedResponseDto>> Handle(
        GetStockInWarehousePagedQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Stock.SapDisabled;

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? 50 : Math.Min(request.PageSize, 200);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var stockItems = await sapClient.GetPagedStockQuantitiesInWarehouseAsync(
                request.WarehouseCode, page, pageSize, cancellationToken);

            var response = new WarehouseStockPagedResponseDto
            {
                WarehouseCode = request.WarehouseCode,
                Page = page,
                PageSize = pageSize,
                Count = stockItems.Count,
                HasMore = stockItems.Count == pageSize,
                QueryDate = DateTime.UtcNow,
                Items = stockItems
            };

            logger.LogInformation("Retrieved page {Page} of stock items in warehouse {Warehouse}, count: {Count}",
                page, request.WarehouseCode, response.Count);

            return response;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Stock.SapTimeout;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "SAP connection aborted for warehouse {Warehouse} (I/O error)", request.WarehouseCode);
            return Errors.Stock.SapConnectionError(ex.InnerException?.Message ?? ex.Message);
        }
        catch (TaskCanceledException)
        {
            // Information, not Error — the caller going away is not a fault. But it has to be said:
            // this branch used to return silently, and a production request that spent exactly
            // 60.000s and then failed left nothing in the log but its Handling/Handled pair. A
            // minute of a user's time with no recorded reason is the worst of both.
            logger.LogInformation(
                "Paged stock request for warehouse {Warehouse} (page {Page}, size {PageSize}) was canceled by the caller after {ElapsedMs} ms.",
                request.WarehouseCode,
                page,
                pageSize,
                timer.ElapsedMilliseconds);
            return Errors.Stock.SapError("Request was cancelled by client.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Stock.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving paged stock for warehouse {Warehouse}", request.WarehouseCode);
            return Errors.Stock.SapError(ex.Message);
        }
    }
}
