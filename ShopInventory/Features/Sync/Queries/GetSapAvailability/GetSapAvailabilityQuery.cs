using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Sync.Queries.GetSapAvailability;

public sealed record GetSapAvailabilityQuery() : IRequest<ErrorOr<SapAvailabilityResult>>;
