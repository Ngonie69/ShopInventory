using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.FiscalPrintForms.Commands.DeleteFiscalPrintForm;

public sealed class DeleteFiscalPrintFormHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<DeleteFiscalPrintFormHandler> logger)
    : IRequestHandler<DeleteFiscalPrintFormCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteFiscalPrintFormCommand request, CancellationToken cancellationToken)
    {
        var cardCode = request.CardCode.Trim();

        var row = await db.BusinessPartnerFiscalPrintForms
            .FirstOrDefaultAsync(existing => existing.CardCode == cardCode, cancellationToken);

        if (row is null)
        {
            return Errors.FiscalisationSettings.PrintFormNotFound(cardCode);
        }

        var before = row.PrintForm;
        db.BusinessPartnerFiscalPrintForms.Remove(row);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Fiscal print form for {CardCode} removed by {User} (was {Before}); back to an A4 invoice",
            cardCode, request.CallerName, before);

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateFiscalPrintForm,
                "BusinessPartnerFiscalPrintForm",
                cardCode,
                $"{cardCode}: was {before}; now the A4 invoice default. Removed by {request.CallerName ?? "unknown"}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the fiscal print form removal for {CardCode}", cardCode);
        }

        return Result.Deleted;
    }
}
