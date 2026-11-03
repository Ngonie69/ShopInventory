using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Common.Http;

namespace ShopInventory.Web.Features.ReportProductGroups.Commands.SaveReportProductGroup;

public sealed class SaveReportProductGroupHandler(
    HttpClient httpClient,
    ILogger<SaveReportProductGroupHandler> logger)
    : IRequestHandler<SaveReportProductGroupCommand, ErrorOr<ReportProductGroupDto>>
{
    public async Task<ErrorOr<ReportProductGroupDto>> Handle(
        SaveReportProductGroupCommand command, CancellationToken cancellationToken)
    {
        var body = new
        {
            name = command.Name,
            description = command.Description,
            sortOrder = command.SortOrder,
            isActive = command.IsActive,
            members = command.Members.Select(m => new { kind = (int)m.Kind, code = m.Code, name = m.Name }).ToList()
        };

        try
        {
            using var response = command.Id is { } id
                ? await httpClient.PutAsJsonAsync($"api/report-product-groups/{id}", body, cancellationToken)
                : await httpClient.PostAsJsonAsync("api/report-product-groups", body, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ProblemDetailReader.ReadMessageAsync(response, cancellationToken);
                logger.LogWarning("Failed to save report product group {Name}. Status code: {StatusCode}. Detail: {Detail}",
                    command.Name, (int)response.StatusCode, detail);
                return Errors.ReportProductGroup.SaveFailed(detail ?? $"Failed to save {command.Name}.");
            }

            var saved = await response.Content.ReadFromJsonAsync<ReportProductGroupDto>(cancellationToken: cancellationToken);
            return saved is null ? Errors.ReportProductGroup.SaveFailed($"Failed to save {command.Name}.") : saved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error saving report product group {Name}", command.Name);
            return Errors.ReportProductGroup.SaveFailed($"Failed to save {command.Name}.");
        }
    }
}
