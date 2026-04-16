using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.GLAccounts.Queries.GetGLAccounts;

public sealed record GetGLAccountsQuery() : IRequest<ErrorOr<GLAccountListResponseDto>>;
