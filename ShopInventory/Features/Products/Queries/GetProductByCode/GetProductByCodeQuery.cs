using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Products.Queries.GetProductByCode;

public sealed record GetProductByCodeQuery(string ItemCode) : IRequest<ErrorOr<ProductDto>>;
