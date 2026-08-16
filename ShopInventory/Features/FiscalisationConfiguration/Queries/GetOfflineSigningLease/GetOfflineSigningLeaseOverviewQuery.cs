using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetOfflineSigningLease;

/// <summary>
/// Every fiscal device the fleet's handsets are registered against, with who is signing offline on each
/// and who else could be. One call, because it is one screen.
/// </summary>
public sealed record GetOfflineSigningLeaseOverviewQuery
    : IRequest<ErrorOr<List<FiscalDeviceOfflineLeaseSummaryDto>>>;
