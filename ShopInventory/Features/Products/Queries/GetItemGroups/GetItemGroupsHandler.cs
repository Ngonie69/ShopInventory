using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Products.Queries.GetItemGroups;

/// <summary>
/// SAP's item groups, so a group code on an item can be shown as a name. Read straight from SAP
/// rather than from the API's own tables — its Products table is never populated.
/// </summary>
public sealed class GetItemGroupsHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetItemGroupsHandler> logger
) : IRequestHandler<GetItemGroupsQuery, ErrorOr<ItemGroupsListResponseDto>>
{
    public async Task<ErrorOr<ItemGroupsListResponseDto>> Handle(
        GetItemGroupsQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Product.SapDisabled;

        try
        {
            var groups = await sapClient.GetItemGroupsAsync(cancellationToken);

            logger.LogInformation("Retrieved {Count} item groups from SAP", groups.Count);

            return new ItemGroupsListResponseDto
            {
                Count = groups.Count,
                Groups = groups
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout reading item groups from SAP");
            return Errors.Product.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error reading item groups from SAP");
            return Errors.Product.SapConnectionError(ex.Message);
        }
    }
}
