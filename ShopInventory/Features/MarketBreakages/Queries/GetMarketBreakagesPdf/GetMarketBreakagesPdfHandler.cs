using ErrorOr;
using MediatR;
using ShopInventory.Features.MarketBreakages.Queries.ExportMarketBreakages;
using ShopInventory.Services;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakagesPdf;

/// <remarks>The filter is checked by the export query this renders.</remarks>
public sealed class GetMarketBreakagesPdfHandler(IMediator mediator)
    : IRequestHandler<GetMarketBreakagesPdfQuery, ErrorOr<MarketBreakagesDocument>>
{
    public async Task<ErrorOr<MarketBreakagesDocument>> Handle(
        GetMarketBreakagesPdfQuery request, CancellationToken cancellationToken)
    {
        var export = await mediator.Send(new ExportMarketBreakagesQuery(request.Status, request.Search), cancellationToken);
        if (export.IsError)
        {
            return export.Errors;
        }

        var generatedAtCat = AuditService.ToCAT(export.Value.GeneratedAtUtc);
        var content = MarketBreakagesPdfRenderer.Render(export.Value, generatedAtCat);
        var scope = MarketBreakagesPdfRenderer.ScopeLabel(export.Value.Status).Replace(' ', '_');

        return new MarketBreakagesDocument(content, $"Market_Breakages_{scope}_{generatedAtCat:yyyyMMdd_HHmm}.pdf");
    }
}
