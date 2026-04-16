using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Queries.GetSapSalesItems;

public sealed class GetSapSalesItemsHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetSapSalesItemsHandler> logger
) : IRequestHandler<GetSapSalesItemsQuery, ErrorOr<List<SapSalesItemDto>>>
{
    public async Task<ErrorOr<List<SapSalesItemDto>>> Handle(
        GetSapSalesItemsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var sqlText = "SELECT T0.\"ItemCode\", T0.\"ItemName\", T0.\"U_ItemGroup\" FROM OITM T0 WHERE T0.\"U_Active\" ='Yes' ORDER BY T0.\"ItemCode\"";

            var rows = await sapClient.ExecuteRawSqlQueryAsync(
                "MerchSalesItems",
                "Merchandiser Sales Items",
                sqlText,
                cancellationToken);

            var items = rows.Select(r => new SapSalesItemDto
            {
                ItemCode = r.GetValueOrDefault("ItemCode")?.ToString() ?? "",
                ItemName = r.GetValueOrDefault("ItemName")?.ToString() ?? "",
                ItemGroup = r.GetValueOrDefault("U_ItemGroup")?.ToString()
            }).ToList();

            return items;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch SAP sales items for merchandiser assignment");
            return Errors.Merchandiser.SapError(ex.Message);
        }
    }
}
