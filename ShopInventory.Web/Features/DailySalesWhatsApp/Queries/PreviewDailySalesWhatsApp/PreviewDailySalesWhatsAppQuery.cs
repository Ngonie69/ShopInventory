using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailySalesWhatsApp.Queries.PreviewDailySalesWhatsApp;

/// <summary>The message the report would send for a day, laid out by the saved settings; yesterday when null.</summary>
public sealed record PreviewDailySalesWhatsAppQuery(DateTime? TradingDate) : IRequest<ErrorOr<DailySalesWhatsAppPreview>>;
