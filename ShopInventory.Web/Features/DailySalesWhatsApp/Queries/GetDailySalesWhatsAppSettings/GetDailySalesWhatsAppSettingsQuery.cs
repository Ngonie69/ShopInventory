using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;

/// <summary>The daily sales WhatsApp report's saved settings.</summary>
public sealed record GetDailySalesWhatsAppSettingsQuery : IRequest<ErrorOr<DailySalesWhatsAppSettings>>;
