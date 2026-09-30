using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalPrintForms.Commands.SaveFiscalPrintForm;

/// <summary>
/// Creates or replaces a partner's choice. It takes effect on the next sale fiscalised for the partner;
/// a receipt already filed keeps the form it was filed as.
/// </summary>
public sealed class SaveFiscalPrintFormHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<SaveFiscalPrintFormHandler> logger)
    : IRequestHandler<SaveFiscalPrintFormCommand, ErrorOr<FiscalPrintFormDto>>
{
    public async Task<ErrorOr<FiscalPrintFormDto>> Handle(
        SaveFiscalPrintFormCommand request, CancellationToken cancellationToken)
    {
        var cardCode = request.CardCode.Trim();
        var printForm = Enum.Parse<ReceiptPrintForm>(request.PrintForm.Trim(), ignoreCase: true);

        var row = await db.BusinessPartnerFiscalPrintForms
            .FirstOrDefaultAsync(existing => existing.CardCode == cardCode, cancellationToken);

        var before = row?.PrintForm.ToString() ?? "none (A4 invoice)";

        if (row is null)
        {
            row = new BusinessPartnerFiscalPrintFormEntity { CardCode = cardCode };
            db.BusinessPartnerFiscalPrintForms.Add(row);
        }

        row.CardName = string.IsNullOrWhiteSpace(request.CardName) ? row.CardName : request.CardName.Trim();
        row.PrintForm = printForm;
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = request.CallerName;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Fiscal print form for {CardCode} set to {PrintForm} by {User} (was {Before})",
            cardCode, printForm, request.CallerName, before);

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateFiscalPrintForm,
                "BusinessPartnerFiscalPrintForm",
                cardCode,
                $"{cardCode}: was {before}; now {printForm}. Saved by {request.CallerName ?? "unknown"}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the fiscal print form change for {CardCode}", cardCode);
        }

        return FiscalPrintFormProjection.ToDto(row);
    }
}
