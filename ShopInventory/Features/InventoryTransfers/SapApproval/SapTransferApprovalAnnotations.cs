using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.SapApproval;

/// <summary>
/// Says on each open transfer request whether SAP's own approval covers its route, so the Transfers page
/// can tell the person converting it that the transfer will go to SAP as a draft rather than post.
/// </summary>
public static class SapTransferApprovalAnnotations
{
    /// <summary>
    /// Fills <see cref="InventoryTransferRequestDto.SapApprovalTemplates"/> on every open request. One
    /// template read serves the page (it is kept for minutes); a failed read leaves the requests marked
    /// unknown rather than failing the list.
    /// </summary>
    public static async Task AnnotateAsync(
        this ISapTransferApprovalRoutes routes,
        IEnumerable<InventoryTransferRequestDto> requests,
        CancellationToken cancellationToken)
    {
        foreach (var request in requests)
        {
            if (string.Equals(request.DocumentStatus, "bost_Close", StringComparison.OrdinalIgnoreCase))
                continue;

            var route = await routes.TryForRouteAsync(
                request.FromWarehouse,
                request.ToWarehouse,
                (request.Lines ?? []).SelectMany(line => new[] { line.FromWarehouseCode, line.ToWarehouseCode }),
                cancellationToken);

            request.SapApprovalKnown = route is not null;
            request.SapApprovalTemplates = route is { IsCovered: true }
                ? route.Templates.Select(template => template.Name).ToList()
                : [];
        }
    }
}
