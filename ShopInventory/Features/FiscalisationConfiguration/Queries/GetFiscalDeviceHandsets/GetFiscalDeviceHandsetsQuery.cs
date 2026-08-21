using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalDeviceHandsets;

/// <summary>Every account that could carry a fiscal device, whether or not it carries one today.</summary>
public sealed record GetFiscalDeviceHandsetsQuery : IRequest<ErrorOr<List<FiscalDeviceHandsetDto>>>;

/// <summary>
/// The handsets the office picks from when registering a device.
/// </summary>
/// <remarks>
/// Broader than the offline signing overview's candidate list, and deliberately so. That list answers
/// "who could be nominated for this device", which only accounts already registered against it can be.
/// This one answers "who could be given a device", which is every active van account — including the ones
/// that have never had one, since those are exactly the accounts a new device is for.
///
/// Accounts already carrying a device are listed too, with the device they carry, so the office can see
/// at a glance that VAN002 is on device 3 rather than discovering it in a refusal.
/// </remarks>
public sealed class GetFiscalDeviceHandsetsHandler(ApplicationDbContext db)
    : IRequestHandler<GetFiscalDeviceHandsetsQuery, ErrorOr<List<FiscalDeviceHandsetDto>>>
{
    public async Task<ErrorOr<List<FiscalDeviceHandsetDto>>> Handle(
        GetFiscalDeviceHandsetsQuery query,
        CancellationToken cancellationToken)
    {
        var handsets = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive)
            .ToListAsync(cancellationToken);

        return handsets
            .Where(user => ApplicationRoles.SupportsFiscalDevice(user.Role))
            .Select(user => new FiscalDeviceHandsetDto
            {
                UserId = user.Id,
                Label = OfflineSigningLeaseMapper.Label(user),
                Role = user.Role,
                FiscalDeviceId = user.FiscalDeviceId
            })
            .OrderBy(handset => handset.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
