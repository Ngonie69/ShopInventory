using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.CustomerDocuments;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;
using ShopInventory.Models;
using ShopInventory.Services;
using Keys = ShopInventory.Features.DailySalesWhatsApp.DailySalesWhatsAppKeys;

namespace ShopInventory.Features.DailySalesWhatsApp.Commands.UpdateDailySalesWhatsAppSettings;

public sealed class UpdateDailySalesWhatsAppSettingsHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IAuditService auditService,
    IOptions<CustomerDocumentDeliverySettings> documentSettings,
    ILogger<UpdateDailySalesWhatsAppSettingsHandler> logger)
    : IRequestHandler<UpdateDailySalesWhatsAppSettingsCommand, ErrorOr<DailySalesWhatsAppSettings>>
{
    public async Task<ErrorOr<DailySalesWhatsAppSettings>> Handle(
        UpdateDailySalesWhatsAppSettingsCommand request, CancellationToken cancellationToken)
    {
        var recipients = new List<string>();
        foreach (var typed in request.Recipients.Where(number => !string.IsNullOrWhiteSpace(number)))
        {
            if (!WhatsAppRecipients.TryNormalise(typed, documentSettings.Value.DefaultCountryCode, out var e164))
            {
                return Errors.DailySalesWhatsApp.InvalidNumber(typed.Trim());
            }

            if (!recipients.Contains(e164, StringComparer.Ordinal))
            {
                recipients.Add(e164);
            }
        }

        var warehouse = string.IsNullOrWhiteSpace(request.ShopWarehouseCode) ? null : request.ShopWarehouseCode.Trim();
        if (warehouse is not null && !await db.Shops.AsNoTracking().AnyAsync(shop => shop.WarehouseCode == warehouse, cancellationToken))
        {
            return Errors.DailySalesWhatsApp.UnknownShop(warehouse);
        }

        var vanRows = Clean(request.VanRows, keepArea: true);
        var productRows = Clean(request.ProductRows, keepArea: false);
        var vanValue = Keys.SerializeRows(vanRows);
        var productValue = Keys.SerializeRows(productRows);
        if (vanValue.Length > Keys.MaxValueLength || productValue.Length > Keys.MaxValueLength)
        {
            return Errors.DailySalesWhatsApp.TooLong;
        }

        var heading = string.IsNullOrWhiteSpace(request.ShopHeading) ? null : request.ShopHeading.Trim();
        var who = string.IsNullOrWhiteSpace(request.CallerName) ? "an admin" : request.CallerName.Trim();

        await Keys.StageAsync(db, Keys.Enabled, "bool", request.Enabled ? "true" : "false",
            "Whether the daily sales report goes out on WhatsApp every morning from 07:15 CAT for the day before.", cancellationToken);
        await Keys.StageAsync(db, Keys.Recipients, "string", string.Join(",", recipients),
            "The WhatsApp numbers that receive the daily sales report, E.164, comma-separated.", cancellationToken);
        await Keys.StageAsync(db, Keys.ShopWarehouseCode, "string", warehouse,
            "The warehouse of the one shop whose till sales the daily sales report lists. Blank leaves the shop table out.", cancellationToken);
        await Keys.StageAsync(db, Keys.ShopHeading, "string", heading,
            "What the daily sales report heads the shop table with. Blank for the shop's name and 'Sales'.", cancellationToken);
        await Keys.StageAsync(db, Keys.VanRows, "json", vanValue,
            "The daily sales report's van rows: a label and the route codes it adds up. Empty lists every route.", cancellationToken);
        await Keys.StageAsync(db, Keys.ProductRows, "json", productValue,
            "The daily sales report's shop rows: a label and the item codes whose quantities it adds up.", cancellationToken);
        await Keys.StageAsync(db, Keys.UpdatedBy, "string", who,
            "Who last saved the daily sales WhatsApp report.", cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Daily sales WhatsApp report saved by {User}: {State}, {Recipients} number(s), shop {Shop}, {VanRows} van row(s), {ProductRows} product row(s)",
            who, request.Enabled ? "on" : "off", recipients.Count, warehouse ?? "none", vanRows.Count, productRows.Count);

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateDailySalesWhatsAppSettings,
                "DailySalesWhatsApp",
                null,
                $"Daily sales WhatsApp report {(request.Enabled ? "on" : "off")}, to "
                    + (recipients.Count == 0 ? "nobody" : string.Join(", ", recipients.Select(WhatsAppRecipients.Mask)))
                    + $", shop {warehouse ?? "none"}, {vanRows.Count} van and {productRows.Count} product row(s), saved by {who}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the daily sales WhatsApp settings change");
        }

        return await mediator.Send(new GetDailySalesWhatsAppSettingsQuery(), cancellationToken);
    }

    private static List<DailySalesWhatsAppRow> Clean(IEnumerable<DailySalesWhatsAppRow> rows, bool keepArea) =>
        rows.Select(row => new DailySalesWhatsAppRow(row.Label.Trim(), Keys.Codes(row.Codes), keepArea && row.ShowArea))
            .ToList();
}
