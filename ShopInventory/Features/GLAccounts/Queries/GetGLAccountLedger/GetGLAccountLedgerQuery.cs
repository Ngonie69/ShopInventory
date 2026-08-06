using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.GLAccounts.Queries.GetGLAccountLedger;

public sealed record GetGLAccountLedgerQuery(
    string AccountCode,
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<GLAccountLedgerResponseDto>>;
