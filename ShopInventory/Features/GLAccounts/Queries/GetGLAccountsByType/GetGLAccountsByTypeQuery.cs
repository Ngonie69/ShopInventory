using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.GLAccounts.Queries.GetGLAccountsByType;

public sealed record GetGLAccountsByTypeQuery(string AccountType) : IRequest<ErrorOr<GLAccountListResponseDto>>;
