using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Common.Http;

namespace ShopInventory.Web.Features.ReportProductGroups.Commands.DeleteReportProductGroup;

public sealed class DeleteReportProductGroupHandler(
    HttpClient httpClient,
    ILogger<DeleteReportProductGroupHandler> logger)
    : IRequestHandler<DeleteReportProductGroupCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(DeleteReportProductGroupCommand command, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.DeleteAsync($"api/report-product-groups/{command.Id}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ProblemDetailReader.ReadMessageAsync(response, cancellationToken);
                logger.LogWarning("Failed to delete report product group {Id}. Status code: {StatusCode}. Detail: {Detail}",
                    command.Id, (int)response.StatusCode, detail);
                return Errors.ReportProductGroup.DeleteFailed(detail ?? "Failed to delete the product group.");
            }

            return Result.Deleted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error deleting report product group {Id}", command.Id);
            return Errors.ReportProductGroup.DeleteFailed("Failed to delete the product group.");
        }
    }
}
