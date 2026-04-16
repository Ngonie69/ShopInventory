using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Stock.Queries.GetWarehouseCodes;

public sealed record GetWarehouseCodesQuery(bool IncludeInactive = false) : IRequest<ErrorOr<List<string>>>;
