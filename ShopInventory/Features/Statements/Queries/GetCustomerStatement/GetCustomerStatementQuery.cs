using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Statements.Queries.GetCustomerStatement;

public sealed record GetCustomerStatementQuery(
    string CardCode,
    DateTime? FromDate,
    DateTime? ToDate,
    IReadOnlyList<string>? CardCodes = null
) : IRequest<ErrorOr<CustomerStatementResponseDto>>;
