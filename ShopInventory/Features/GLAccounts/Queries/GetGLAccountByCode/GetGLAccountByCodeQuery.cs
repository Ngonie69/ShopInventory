using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.GLAccounts.Queries.GetGLAccountByCode;

public sealed record GetGLAccountByCodeQuery(string AccountCode) : IRequest<ErrorOr<GLAccountDto>>;
