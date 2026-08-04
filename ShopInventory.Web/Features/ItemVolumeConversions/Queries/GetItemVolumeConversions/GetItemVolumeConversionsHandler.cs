using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;

namespace ShopInventory.Web.Features.ItemVolumeConversions.Queries.GetItemVolumeConversions;

public sealed class GetItemVolumeConversionsHandler(
    HttpClient httpClient,
    ILogger<GetItemVolumeConversionsHandler> logger
) : IRequestHandler<GetItemVolumeConversionsQuery, ErrorOr<GetItemVolumeConversionsResult>>
{
    public async Task<ErrorOr<GetItemVolumeConversionsResult>> Handle(
        GetItemVolumeConversionsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var queryParts = new List<string>
            {
                $"includeInactive={request.IncludeInactive.ToString().ToLowerInvariant()}"
            };

            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                queryParts.Add($"search={Uri.EscapeDataString(request.Search.Trim())}");
            }

            var url = $"api/itemvolumeconversion?{string.Join("&", queryParts)}";
            var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Failed to load item volume conversions from {Url}. Status code: {StatusCode}. Body: {Body}",
                    url,
                    (int)response.StatusCode,
                    body);

                return Errors.ItemVolumeConversion.LoadFailed("Failed to load the volume conversion factors.");
            }

            var result = await response.Content.ReadFromJsonAsync<GetItemVolumeConversionsResult>(
                cancellationToken: cancellationToken);

            return result is null
                ? Errors.ItemVolumeConversion.LoadFailed("Failed to load the volume conversion factors.")
                : result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading item volume conversions in web CQRS handler");
            return Errors.ItemVolumeConversion.LoadFailed("Failed to load the volume conversion factors.");
        }
    }
}
