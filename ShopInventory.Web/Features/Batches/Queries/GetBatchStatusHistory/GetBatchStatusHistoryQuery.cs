using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Batches.Queries.GetBatchStatusHistory;

public sealed record GetBatchStatusHistoryQuery(
    string? SearchTerm,
    int Page = 1,
    int PageSize = 25
) : IRequest<ErrorOr<BatchStatusHistoryResponse>>;