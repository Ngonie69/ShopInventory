using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetOfflineSigningLease;

/// <summary>
/// Builds the office's view of offline signing: one entry per fiscal device the fleet actually uses.
/// </summary>
/// <remarks>
/// The devices come from the handsets rather than from a device table, because a device nobody is
/// registered against is not a thing the office can nominate anyone for. A fleet sharing one device
/// therefore sees exactly one entry, which is the intended shape rather than a coincidence.
/// </remarks>
public sealed class GetOfflineSigningLeaseOverviewHandler(ApplicationDbContext db)
    : IRequestHandler<GetOfflineSigningLeaseOverviewQuery, ErrorOr<List<FiscalDeviceOfflineLeaseSummaryDto>>>
{
    public async Task<ErrorOr<List<FiscalDeviceOfflineLeaseSummaryDto>>> Handle(
        GetOfflineSigningLeaseOverviewQuery query,
        CancellationToken cancellationToken)
    {
        var handsets = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && user.FiscalDeviceId != null && user.FiscalDeviceId > 0)
            .ToListAsync(cancellationToken);

        if (handsets.Count == 0)
        {
            return new List<FiscalDeviceOfflineLeaseSummaryDto>();
        }

        var deviceIds = handsets
            .Select(user => user.FiscalDeviceId!.Value)
            .Distinct()
            .OrderBy(deviceId => deviceId)
            .ToList();

        var nominations = await db.FiscalDeviceOfflineLeases
            .AsNoTracking()
            .Where(row => deviceIds.Contains(row.DeviceId))
            .ToDictionaryAsync(row => row.DeviceId, cancellationToken);

        return deviceIds
            .Select(deviceId => new FiscalDeviceOfflineLeaseSummaryDto
            {
                Lease = nominations.TryGetValue(deviceId, out var nomination)
                    ? OfflineSigningLeaseMapper.ToDto(nomination)
                    : OfflineSigningLeaseMapper.Unassigned(deviceId),

                Candidates = handsets
                    .Where(user => user.FiscalDeviceId == deviceId)
                    .Select(user => new OfflineSigningCandidateDto
                    {
                        UserId = user.Id,
                        Label = OfflineSigningLeaseMapper.Label(user)
                    })
                    .OrderBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();
    }
}
