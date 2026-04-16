using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Sync.Queries.GetHealthSummary;

public sealed record GetHealthSummaryQuery() : IRequest<ErrorOr<SyncHealthSummaryDto>>;
