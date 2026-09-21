using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.VanSalesDocuments.Commands.RetryVanSalesInvoiceFiscalisation;

/// <summary>
/// Sends the retry through the desktop integration endpoint and hands back the API's own answer.
/// </summary>
/// <remarks>
/// The device's refusal is the one thing the operator pressed Retry to read, so the API's sentence is
/// returned as the error rather than summarised.
/// </remarks>
public sealed class RetryVanSalesInvoiceFiscalisationHandler(
    IDesktopIntegrationService desktop,
    ILogger<RetryVanSalesInvoiceFiscalisationHandler> logger
) : IRequestHandler<RetryVanSalesInvoiceFiscalisationCommand, ErrorOr<DesktopSaleFiscalisationRetryResultDto>>
{
    public async Task<ErrorOr<DesktopSaleFiscalisationRetryResultDto>> Handle(
        RetryVanSalesInvoiceFiscalisationCommand request,
        CancellationToken cancellationToken)
    {
        var (result, error) = await desktop.RetrySaleFiscalisationAsync(request.Reference, cancellationToken);

        if (error is not null || result is null)
        {
            logger.LogWarning(
                "Van invoice {Reference} was not fiscalised on request: {Error}",
                request.Reference,
                error);

            return Errors.VanSalesDocument.FiscaliseFailed(
                error ?? "The API accepted the retry but returned nothing to show for it.");
        }

        logger.LogInformation(
            "Van invoice {Reference} fiscalised on request: {Outcome}, receipt {Receipt}",
            request.Reference,
            result.Outcome,
            result.FiscalReceiptNumber);

        return result;
    }
}
