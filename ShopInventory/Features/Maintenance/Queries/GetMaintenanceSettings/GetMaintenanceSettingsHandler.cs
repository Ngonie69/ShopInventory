using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Maintenance.Queries.GetMaintenanceSettings;

public sealed class GetMaintenanceSettingsHandler(
    IMaintenanceStore store,
    TimeProvider timeProvider,
    ILogger<GetMaintenanceSettingsHandler> logger
) : IRequestHandler<GetMaintenanceSettingsQuery, ErrorOr<MaintenanceSettingsDto>>
{
    public async Task<ErrorOr<MaintenanceSettingsDto>> Handle(
        GetMaintenanceSettingsQuery request,
        CancellationToken cancellationToken)
    {
        // Read through rather than off the snapshot: a screen that exists to show what the switch
        // says should not show a value up to the refresh interval out of date, and unlike the
        // request path this happens once, when somebody opens the page.
        try
        {
            await store.ReloadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The database being unreachable is not a reason to refuse this screen — it is very
            // likely the reason maintenance is running at all, and this is the screen somebody
            // opens to turn it back off. The snapshot is at worst a few seconds stale.
            logger.LogWarning(ex, "Could not reload the maintenance switch; showing the last known state.");
        }

        ErrorOr<MaintenanceSettingsDto> result =
            MaintenanceMapper.ToSettings(store.Current, timeProvider.GetUtcNow().UtcDateTime);

        return result;
    }
}
