using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.FiscalPrintForms.Commands.SaveFiscalPrintForm;

public sealed class SaveFiscalPrintFormHandler(HttpClient httpClient, ILogger<SaveFiscalPrintFormHandler> logger)
    : IRequestHandler<SaveFiscalPrintFormCommand, ErrorOr<FiscalPrintForm>>
{
    public Task<ErrorOr<FiscalPrintForm>> Handle(SaveFiscalPrintFormCommand request, CancellationToken cancellationToken) =>
        FiscalPrintFormsApi.SendAsync<FiscalPrintForm>(
            httpClient,
            logger,
            HttpMethod.Put,
            FiscalPrintFormsApi.For(request.CardCode),
            request.Body,
            "save the document type",
            cancellationToken);
}
