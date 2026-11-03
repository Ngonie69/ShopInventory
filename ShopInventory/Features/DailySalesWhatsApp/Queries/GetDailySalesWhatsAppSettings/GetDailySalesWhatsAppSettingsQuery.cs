using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;

/// <summary>The daily sales WhatsApp report's settings, as last saved from Settings.</summary>
public sealed record GetDailySalesWhatsAppSettingsQuery : IRequest<ErrorOr<DailySalesWhatsAppSettings>>;
