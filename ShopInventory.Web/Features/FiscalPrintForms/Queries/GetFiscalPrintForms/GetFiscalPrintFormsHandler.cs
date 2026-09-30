using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.FiscalPrintForms.Queries.GetFiscalPrintForms;

public sealed class GetFiscalPrintFormsHandler(HttpClient httpClient, ILogger<GetFiscalPrintFormsHandler> logger)
    : IRequestHandler<GetFiscalPrintFormsQuery, ErrorOr<List<FiscalPrintForm>>>
{
    public Task<ErrorOr<List<FiscalPrintForm>>> Handle(GetFiscalPrintFormsQuery request, CancellationToken cancellationToken) =>
        FiscalPrintFormsApi.SendAsync<List<FiscalPrintForm>>(
            httpClient,
            logger,
            HttpMethod.Get,
            FiscalPrintFormsApi.Base,
            null,
            "load the document types",
            cancellationToken);
}
