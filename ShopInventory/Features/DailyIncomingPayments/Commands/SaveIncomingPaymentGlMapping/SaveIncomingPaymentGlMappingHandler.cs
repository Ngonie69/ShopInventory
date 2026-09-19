using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DailyIncomingPayments.Commands.SaveIncomingPaymentGlMapping;

/// <summary>
/// Saves a partner's mapping once both accounts are confirmed in SAP. A typo in an account code would
/// otherwise only show up when the 17:00 payment is refused.
/// </summary>
public sealed class SaveIncomingPaymentGlMappingHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
    ILogger<SaveIncomingPaymentGlMappingHandler> logger)
    : IRequestHandler<SaveIncomingPaymentGlMappingCommand, ErrorOr<IncomingPaymentGlMappingDto>>
{
    public async Task<ErrorOr<IncomingPaymentGlMappingDto>> Handle(
        SaveIncomingPaymentGlMappingCommand request, CancellationToken cancellationToken)
    {
        var cardCode = request.CardCode.Trim();
        var cash = request.CashAccount.Trim();
        var electronic = request.ElectronicAccount.Trim();

        foreach (var code in new[] { cash, electronic }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var error = await CheckAccountAsync(code, cancellationToken);
            if (error is not null)
            {
                return error.Value;
            }
        }

        var mapping = await db.IncomingPaymentGlMappings
            .FirstOrDefaultAsync(row => row.CardCode == cardCode, cancellationToken);

        var before = mapping is null
            ? "none"
            : $"cash {mapping.CashAccount}, electronic {mapping.ElectronicAccount}, {mapping.Run}, {(mapping.IsActive ? "active" : "inactive")}";

        if (mapping is null)
        {
            mapping = new IncomingPaymentGlMappingEntity { CardCode = cardCode };
            db.IncomingPaymentGlMappings.Add(mapping);
        }

        mapping.CardName = string.IsNullOrWhiteSpace(request.CardName) ? mapping.CardName : request.CardName.Trim();
        mapping.CashAccount = cash;
        mapping.ElectronicAccount = electronic;
        mapping.Run = Enum.Parse<DailyPaymentRun>(request.Run, ignoreCase: true);
        mapping.NotifyEmails = IncomingPaymentGlMappingAddresses.Normalise(request.NotifyEmails);
        mapping.IsActive = request.IsActive;
        mapping.NeedsReview = false;
        mapping.UpdatedAtUtc = DateTime.UtcNow;
        mapping.UpdatedBy = request.CallerName;

        await db.SaveChangesAsync(cancellationToken);

        var after = $"cash {cash}, electronic {electronic}, {mapping.Run}, {(mapping.IsActive ? "active" : "inactive")}, "
                    + $"emails {mapping.NotifyEmails ?? "none"}";

        logger.LogWarning(
            "Daily payment G/L mapping for {CardCode} saved by {User}: was {Before}; now {After}",
            cardCode, request.CallerName, before, after);

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateIncomingPaymentGlMapping,
                "IncomingPaymentGlMapping",
                cardCode,
                $"{cardCode}: was {before}; now {after}. Saved by {request.CallerName ?? "unknown"}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the G/L mapping change for {CardCode}", cardCode);
        }

        return IncomingPaymentGlMappingProjection.ToDto(mapping);
    }

    private async Task<Error?> CheckAccountAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            var account = await sapClient.GetGLAccountByCodeAsync(code, cancellationToken);
            if (account is null)
            {
                return Errors.DailyIncomingPayment.UnknownAccount(code);
            }

            return account.IsActive ? null : Errors.DailyIncomingPayment.InactiveAccount(code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Errors.DailyIncomingPayment.SapUnavailable(ex.Message);
        }
    }
}
