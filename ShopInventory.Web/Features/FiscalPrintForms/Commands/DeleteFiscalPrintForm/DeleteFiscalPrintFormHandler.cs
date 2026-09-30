using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.FiscalPrintForms.Commands.DeleteFiscalPrintForm;

public sealed class DeleteFiscalPrintFormHandler(HttpClient httpClient, ILogger<DeleteFiscalPrintFormHandler> logger)
    : IRequestHandler<DeleteFiscalPrintFormCommand, ErrorOr<Success>>
{
    public Task<ErrorOr<Success>> Handle(DeleteFiscalPrintFormCommand request, CancellationToken cancellationToken) =>
        FiscalPrintFormsApi.SendAsync(
            httpClient,
            logger,
            HttpMethod.Delete,
            FiscalPrintFormsApi.For(request.CardCode),
            "remove the document type",
            cancellationToken);
}
