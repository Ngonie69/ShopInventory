using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Maintenance.Queries.GetMobileMaintenanceSettings;

public sealed class GetMobileMaintenanceSettingsHandler(
    IMobileMaintenanceStore store,
    TimeProvider timeProvider,
    ILogger<GetMobileMaintenanceSettingsHandler> logger
) : IRequestHandler<GetMobileMaintenanceSettingsQuery, ErrorOr<MobileMaintenanceSettingsDto>>
{
    public async Task<ErrorOr<MobileMaintenanceSettingsDto>> Handle(
        GetMobileMaintenanceSettingsQuery request,
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
            logger.LogWarning(ex, "Could not reload the mobile maintenance switch; showing the last known state.");
        }

        ErrorOr<MobileMaintenanceSettingsDto> result =
            MobileMaintenanceMapper.ToSettings(store.Current, timeProvider.GetUtcNow().UtcDateTime);

        return result;
    }
}
