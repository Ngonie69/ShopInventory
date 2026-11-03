using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppReport;

/// <summary>
/// The daily sales WhatsApp report for one trading day, laid out by the saved settings — what would be
/// sent, without sending it.
/// </summary>
/// <param name="TradingDate">The CAT trading day.</param>
public sealed record GetDailySalesWhatsAppReportQuery(DateTime TradingDate) : IRequest<ErrorOr<DailySalesWhatsAppReport>>;
