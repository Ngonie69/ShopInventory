using ErrorOr;
using MediatR;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;

namespace ShopInventory.Features.DailySalesWhatsApp.Commands.UpdateDailySalesWhatsAppSettings;

/// <summary>
/// Saves whether the daily sales report goes out on WhatsApp, to which numbers, for which shop, and the
/// rows of its two tables.
/// </summary>
/// <param name="CallerName">Who saved it, for the audit trail and the settings card.</param>
/// <param name="Enabled">Whether the job sends it each morning.</param>
/// <param name="Recipients">Numbers as typed; a local number takes the default country code.</param>
/// <param name="ShopWarehouseCode">The shop's warehouse; blank leaves the shop table out.</param>
/// <param name="ShopHeading">What the shop table is headed; blank for "{shop name} Sales".</param>
/// <param name="VanRows">The van table's rows in order; empty lists every route under its own name.</param>
/// <param name="ProductRows">The shop table's rows in order.</param>
public sealed record UpdateDailySalesWhatsAppSettingsCommand(
    string? CallerName,
    bool Enabled,
    List<string> Recipients,
    string? ShopWarehouseCode,
    string? ShopHeading,
    List<DailySalesWhatsAppRow> VanRows,
    List<DailySalesWhatsAppRow> ProductRows
) : IRequest<ErrorOr<DailySalesWhatsAppSettings>>;
