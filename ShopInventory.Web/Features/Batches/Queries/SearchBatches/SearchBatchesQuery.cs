using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Batches.Queries.SearchBatches;

public sealed record SearchBatchesQuery(string SearchTerm) : IRequest<ErrorOr<BatchSearchResponse>>;