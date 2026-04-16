using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Sync.Queries.GetQueuedItems;

public sealed record GetQueuedItemsQuery() : IRequest<ErrorOr<List<QueuedTransactionDto>>>;
