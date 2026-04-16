using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Statements.Queries.GenerateStatement;

public sealed record GenerateStatementResult(byte[] PdfBytes, string FileName);

public sealed record GenerateStatementQuery(
    string CardCode,
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<GenerateStatementResult>>;
