using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.RouteCustomers.Commands.ImportRouteSchedule;

/// <summary>
/// Sends the outlets to the API and carries its verdict back, recording a save in the audit log.
/// </summary>
/// <remarks>
/// Which shop each name means, which truck a sheet is for and what saving replaces are all the API's
/// answers. An outlet it could not place comes back inside the result for the page to show against its
/// row; only a request it would not look at becomes an error here.
/// </remarks>
public sealed class ImportRouteScheduleHandler(
    IRouteCustomerService routeCustomerService,
    IAuditService auditService,
    ILogger<ImportRouteScheduleHandler> logger
) : IRequestHandler<ImportRouteScheduleCommand, ErrorOr<RouteScheduleImportResultModel>>
{
    public async Task<ErrorOr<RouteScheduleImportResultModel>> Handle(
        ImportRouteScheduleCommand request,
        CancellationToken cancellationToken)
    {
        var (result, error) = await routeCustomerService.ImportScheduleAsync(request.Request, cancellationToken);

        if (result is null)
        {
            if (!request.Request.ValidateOnly)
            {
                await TryAuditAsync(null, error);
            }

            return Errors.RouteSchedule.ImportFailed(error ?? "The calling schedule could not be imported.");
        }

        if (result.Applied)
        {
            await TryAuditAsync(result, null);
        }

        return result;
    }

    private async Task TryAuditAsync(RouteScheduleImportResultModel? result, string? error)
    {
        var saved = result?.Accounts.Where(account => account.Applied).ToList() ?? [];
        var details = result is null
            ? null
            : string.Join("; ", saved.Select(account =>
                $"{account.AccountCode}: {account.ScheduledBefore} -> {account.ScheduledAfter} shops scheduled"));

        try
        {
            await auditService.LogAsync(
                AuditActions.ImportRouteSchedule,
                "RouteCustomerVisitDay",
                saved.Count == 0 ? null : string.Join(",", saved.Select(account => account.AccountCode)),
                details,
                isSuccess: result is not null,
                errorMessage: error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The schedule is saved either way; a missing audit row must not tell the office it was not.
            logger.LogWarning(ex, "Could not audit a calling-schedule import");
        }
    }
}
