using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.ValidateBulkPods;

public sealed record ValidateBulkPodsQuery(
	IReadOnlyList<int> DocNums,
	IReadOnlyList<int> SalesOrderDocNums) : IRequest<ErrorOr<BulkPodValidationResponseDto>>;