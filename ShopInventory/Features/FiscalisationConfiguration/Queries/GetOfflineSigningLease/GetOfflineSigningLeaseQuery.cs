using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetOfflineSigningLease;

public sealed record GetOfflineSigningLeaseQuery(int DeviceId)
    : IRequest<ErrorOr<FiscalDeviceOfflineLeaseDto>>;
