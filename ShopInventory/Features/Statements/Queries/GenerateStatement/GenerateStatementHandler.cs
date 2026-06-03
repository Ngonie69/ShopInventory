using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Statements.Queries.GetCustomerStatement;
using ShopInventory.Services;

namespace ShopInventory.Features.Statements.Queries.GenerateStatement;

public sealed class GenerateStatementHandler(
    ISender sender,
    IStatementService statementService,
    ILogger<GenerateStatementHandler> logger
) : IRequestHandler<GenerateStatementQuery, ErrorOr<GenerateStatementResult>>
{
    public async Task<ErrorOr<GenerateStatementResult>> Handle(
        GenerateStatementQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var statementResult = await sender.Send(
                new GetCustomerStatementQuery(request.CardCode, request.FromDate, request.ToDate, request.CardCodes),
                cancellationToken);

            if (statementResult.IsError)
            {
                return statementResult.Errors;
            }

            var pdfBytes = await statementService.GenerateCustomerStatementAsync(statementResult.Value);

            var fileName = $"Statement_{request.CardCode}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf";

            return new GenerateStatementResult(pdfBytes, fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating statement for {CardCode}", request.CardCode);
            return Errors.Statement.GenerationFailed(ex.Message);
        }
    }
}
