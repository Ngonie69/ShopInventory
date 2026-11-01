using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.DailyIncomingPayments.Commands.SetIncomingPaymentGlMappingActive;

/// <summary>
/// Flips the per-partner switch. The accounts were checked in SAP when the mapping was saved, so this
/// needs no SAP call, and the next pass of the daily payment job honours it.
/// </summary>
public sealed class SetIncomingPaymentGlMappingActiveHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<SetIncomingPaymentGlMappingActiveHandler> logger)
    : IRequestHandler<SetIncomingPaymentGlMappingActiveCommand, ErrorOr<IncomingPaymentGlMappingDto>>
{
    public async Task<ErrorOr<IncomingPaymentGlMappingDto>> Handle(
        SetIncomingPaymentGlMappingActiveCommand request, CancellationToken cancellationToken)
    {
        var cardCode = request.CardCode.Trim();
        var mapping = await db.IncomingPaymentGlMappings
            .FirstOrDefaultAsync(row => row.CardCode == cardCode, cancellationToken);

        if (mapping is null)
        {
            return Errors.DailyIncomingPayment.MappingNotFound(cardCode);
        }

        if (mapping.IsActive == request.IsActive)
        {
            return IncomingPaymentGlMappingProjection.ToDto(mapping);
        }

        var now = DateTime.UtcNow;
        DailyPaymentSwitch.Set(mapping, request.IsActive, now);
        mapping.UpdatedAtUtc = now;
        mapping.UpdatedBy = request.CallerName;

        await db.SaveChangesAsync(cancellationToken);

        var state = request.IsActive ? "ON" : "OFF";
        logger.LogWarning(
            "Daily incoming payments for {CardCode} switched {State} by {User}",
            cardCode, state, request.CallerName);

        try
        {
            await auditService.LogAsync(
                AuditActions.SetIncomingPaymentGlMappingActive,
                "IncomingPaymentGlMapping",
                cardCode,
                $"Daily incoming payments for {cardCode} switched {(request.IsActive ? $"on, paying invoices from {mapping.PaysFrom:yyyy-MM-dd}," : "off")} by {request.CallerName ?? "unknown"}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the daily payment switch for {CardCode}", cardCode);
        }

        return IncomingPaymentGlMappingProjection.ToDto(mapping);
    }
}
