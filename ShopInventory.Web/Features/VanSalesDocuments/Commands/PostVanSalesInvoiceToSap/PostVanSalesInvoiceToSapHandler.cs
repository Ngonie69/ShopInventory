using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.VanSalesDocuments.Commands.PostVanSalesInvoiceToSap;

/// <summary>
/// Sends the post through the desktop integration endpoint and hands back the API's own answer.
/// </summary>
/// <remarks>
/// Every refusal on that route names the sale and says what is wrong with it — not fiscalised, already in
/// SAP, being posted by somebody else, or what SAP said — and that sentence is the one thing the operator
/// pressed the button to read, so it is returned as the error rather than summarised.
/// </remarks>
public sealed class PostVanSalesInvoiceToSapHandler(
    IDesktopIntegrationService desktop,
    ILogger<PostVanSalesInvoiceToSapHandler> logger
) : IRequestHandler<PostVanSalesInvoiceToSapCommand, ErrorOr<DesktopSalePostResultDto>>
{
    public async Task<ErrorOr<DesktopSalePostResultDto>> Handle(
        PostVanSalesInvoiceToSapCommand request,
        CancellationToken cancellationToken)
    {
        var (result, error) = await desktop.PostSaleToSapAsync(request.Reference, cancellationToken);

        if (error is not null || result is null)
        {
            logger.LogWarning(
                "Van invoice {Reference} was not posted to SAP on request: {Error}",
                request.Reference,
                error);

            return Errors.VanSalesDocument.PostFailed(
                error ?? "The API accepted the post but returned nothing to show for it.");
        }

        logger.LogInformation(
            "Van invoice {Reference} sent to SAP on request: {Outcome}, invoice {DocNum}",
            request.Reference,
            result.Outcome,
            result.SapDocNum);

        return result;
    }
}
