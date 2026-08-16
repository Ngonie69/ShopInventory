using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetOfflineSigningLease;

/// <summary>
/// Who may currently sign offline on a fiscal device, and whether that can safely be moved.
/// </summary>
public sealed class GetOfflineSigningLeaseHandler(ApplicationDbContext db)
    : IRequestHandler<GetOfflineSigningLeaseQuery, ErrorOr<FiscalDeviceOfflineLeaseDto>>
{
    public async Task<ErrorOr<FiscalDeviceOfflineLeaseDto>> Handle(
        GetOfflineSigningLeaseQuery query,
        CancellationToken cancellationToken)
    {
        if (query.DeviceId <= 0)
        {
            return Error.Validation(
                "OfflineSigningLease.DeviceRequired",
                "A fiscal device id is needed to say who may sign offline on it.");
        }

        var nomination = await db.FiscalDeviceOfflineLeases
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.DeviceId == query.DeviceId, cancellationToken);

        // A device with no row has never been nominated, which reads the same as nominated to nobody: no
        // handset may sign offline on it. Not stored until someone is, so that the absence of a row and
        // the safe default cannot drift apart.
        return nomination is null
            ? OfflineSigningLeaseMapper.Unassigned(query.DeviceId)
            : OfflineSigningLeaseMapper.ToDto(nomination);
    }
}
