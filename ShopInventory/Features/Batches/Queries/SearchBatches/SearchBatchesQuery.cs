using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Batches.Queries.SearchBatches;

public sealed record SearchBatchesQuery(string SearchTerm) : IRequest<ErrorOr<BatchSearchResponseDto>>;