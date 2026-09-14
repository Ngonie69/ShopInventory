using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.RetryDesktopSaleFiscalisation;

/// <summary>
/// Retries one sale's fiscalisation on request, through the same fiscaliser the till and the sweep use.
/// </summary>
/// <remarks>
/// <para>
/// Thin for the reason <c>PostDesktopSaleToSapHandler</c> is: it decides whether this caller may retry
/// this sale and hands the sale to <see cref="DesktopSaleFiscaliser"/>, so there is one implementation of
/// "sign this sale" and one place a duplicate receipt could come from.
/// </para>
/// <para>
/// <b>The device is always asked first.</b> A person reaches for Retry exactly when an earlier attempt's
/// outcome is in doubt, so this never trusts the attempt counter to say whether one reached the device.
/// If the device cannot be asked, nothing is sent and the sale is left as it was.
/// </para>
/// <para>
/// It can overlap a sweep pass on the same sale. Both ask first, and both submit under the sale's one
/// invoice number, which the device will not accept twice — REVMax refuses the second as a duplicate and
/// the fiscaliser adopts the receipt the first filed.
/// </para>
/// </remarks>
public sealed class RetryDesktopSaleFiscalisationHandler(
    ApplicationDbContext db,
    DesktopSaleFiscaliser fiscaliser,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    IAuditService auditService,
    ILogger<RetryDesktopSaleFiscalisationHandler> logger)
    : IRequestHandler<RetryDesktopSaleFiscalisationCommand, ErrorOr<DesktopSaleFiscalisationRetryResult>>
{
    public async Task<ErrorOr<DesktopSaleFiscalisationRetryResult>> Handle(
        RetryDesktopSaleFiscalisationCommand command,
        CancellationToken cancellationToken)
    {
        var reference = command.ExternalReferenceId.Trim();

        var caller = await db.Users
            .AsNoTracking()
            .Include(user => user.Shop)
            .FirstOrDefaultAsync(user => user.Id == command.CallerUserId, cancellationToken);

        var scope = DesktopSalesReadScopeResolver.Resolve(caller);
        if (scope.IsError)
        {
            return scope.Errors;
        }

        var sale = await db.DesktopSales
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.ExternalReferenceId == reference, cancellationToken);

        if (sale is null)
        {
            return Errors.DesktopSales.SaleNotFound(reference);
        }

        if (!scope.Value.IsUnrestricted &&
            !string.Equals(sale.WarehouseCode, scope.Value.WarehouseCode, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.DesktopSales.SalesReadOutsideScope(sale.WarehouseCode, scope.Value.WarehouseCode!);
        }

        var refusal = DesktopSaleFiscalisationRetry.ManualRefusal(
            sale.SourceSystem,
            sale.FiscalizationStatus,
            sale.FiscalizationRequiresReconciliation,
            sale.CreatedAt,
            DateTime.UtcNow,
            fiscalisationSettings.Value.UsesPlatform);

        if (refusal is not null)
        {
            return Errors.DesktopSales.SaleNotFiscalisable(reference, refusal);
        }

        logger.LogInformation(
            "Retrying fiscalisation of sale {ExternalReference} on request (attempt {Attempt}).",
            reference, sale.FiscalizationAttempts + 1);

        var attemptsBefore = sale.FiscalizationAttempts;

        try
        {
            await fiscaliser.FiscaliseAsync(sale, cancellationToken, isRetry: true);
        }
        catch (Exception ex)
        {
            // Only the look-up throws. Nothing was sent and nothing is saved.
            logger.LogWarning(
                ex, "Could not ask the device whether sale {ExternalReference} is already fiscalised.", reference);

            var detail = $"Nothing was sent: the fiscal device could not be asked whether it already holds this "
                + $"receipt. Try again once it is reachable. {ex.Message}";
            await AuditAsync(reference, succeeded: false, detail);
            return Errors.DesktopSales.SaleFiscalisationUncheckable(reference, detail);
        }

        // Saved whatever happened, and not on the request's token: the receipt may already exist at the
        // device, and losing that to an abandoned request would leave the sale looking unfiscalised.
        await db.SaveChangesAsync(CancellationToken.None);

        switch (sale.FiscalizationStatus)
        {
            case DesktopSaleFiscalizationStatus.Success:
            {
                var adopted = sale.FiscalizationAttempts == attemptsBefore;
                var message = adopted
                    ? $"The device already held receipt {sale.FiscalReceiptNumber} for this sale, so it was adopted and nothing was sent again."
                    : $"Fiscalised as receipt {sale.FiscalReceiptNumber}. It will post to SAP with the next pass.";

                await AuditAsync(reference, succeeded: true, message);

                return new DesktopSaleFiscalisationRetryResult(
                    reference,
                    adopted
                        ? DesktopSaleFiscalisationRetryOutcomes.AlreadyFiscalised
                        : DesktopSaleFiscalisationRetryOutcomes.Fiscalised,
                    sale.FiscalReceiptNumber,
                    message);
            }

            default:
            {
                var detail = sale.FiscalizationRequiresReconciliation
                    ? $"The device could not say whether it signed this receipt. Check the device before retrying. {sale.FiscalError}"
                    : sale.FiscalError ?? "The fiscal device did not sign the receipt.";

                await AuditAsync(reference, succeeded: false, detail);
                return Errors.DesktopSales.SaleFiscalisationFailed(reference, detail);
            }
        }
    }

    /// <remarks>Never allowed to break the retry: the receipt may already be signed by the time this runs.</remarks>
    private async Task AuditAsync(string reference, bool succeeded, string detail)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.RetryDesktopSaleFiscalisation,
                nameof(DesktopSaleEntity),
                reference,
                succeeded
                    ? $"Sale {reference} fiscalised on request: {detail}"
                    : $"Sale {reference} was not fiscalised on request",
                succeeded,
                succeeded ? null : detail);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Failed to audit the fiscalisation retry of sale {ExternalReference}.", reference);
        }
    }
}
