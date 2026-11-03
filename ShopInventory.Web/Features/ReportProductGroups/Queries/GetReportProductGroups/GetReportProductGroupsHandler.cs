using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Common.Http;

namespace ShopInventory.Web.Features.ReportProductGroups.Queries.GetReportProductGroups;

public sealed class GetReportProductGroupsHandler(
    HttpClient httpClient,
    ILogger<GetReportProductGroupsHandler> logger)
    : IRequestHandler<GetReportProductGroupsQuery, ErrorOr<List<ReportProductGroupDto>>>
{
    public async Task<ErrorOr<List<ReportProductGroupDto>>> Handle(
        GetReportProductGroupsQuery request, CancellationToken cancellationToken)
    {
        var url = $"api/report-product-groups?includeInactive={(request.IncludeInactive ? "true" : "false")}";
        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ProblemDetailReader.ReadMessageAsync(response, cancellationToken);
                logger.LogWarning("Failed to load report product groups. Status code: {StatusCode}. Detail: {Detail}",
                    (int)response.StatusCode, detail);
                return Errors.ReportProductGroup.LoadFailed(detail ?? "Failed to load the product groups.");
            }

            return await response.Content.ReadFromJsonAsync<List<ReportProductGroupDto>>(cancellationToken: cancellationToken) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading report product groups");
            return Errors.ReportProductGroup.LoadFailed("Failed to load the product groups.");
        }
    }
}
