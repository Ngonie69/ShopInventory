using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayment;

public sealed class GetDailyIncomingPaymentHandler(ApplicationDbContext db)
    : IRequestHandler<GetDailyIncomingPaymentQuery, ErrorOr<DailyIncomingPaymentDetailDto>>
{
    public async Task<ErrorOr<DailyIncomingPaymentDetailDto>> Handle(
        GetDailyIncomingPaymentQuery request, CancellationToken cancellationToken)
    {
        var header = await db.DailyIncomingPayments
            .AsNoTracking()
            .Where(payment => payment.Id == request.Id)
            .Select(DailyIncomingPaymentProjection.Summary)
            .FirstOrDefaultAsync(cancellationToken);

        if (header is null)
        {
            return Errors.DailyIncomingPayment.NotFound(request.Id);
        }

        var lines = await db.DailyIncomingPaymentLines
            .AsNoTracking()
            .Where(line => line.DailyIncomingPaymentId == request.Id)
            .OrderBy(line => line.Id)
            .ToListAsync(cancellationToken);

        var saleIds = lines.Where(line => line.DesktopSaleId != null).Select(line => line.DesktopSaleId!.Value).ToList();
        var consolidationIds = lines.Where(line => line.SaleConsolidationId != null).Select(line => line.SaleConsolidationId!.Value).ToList();
        var reservationIds = lines.Where(line => line.StockReservationId != null).Select(line => line.StockReservationId!.Value).ToList();

        var sales = await db.DesktopSales
            .AsNoTracking()
            .Where(sale => saleIds.Contains(sale.Id))
            .Select(sale => new { sale.Id, sale.ExternalReferenceId, sale.DocDate, sale.PaymentMethod, sale.SourceSystem })
            .ToDictionaryAsync(sale => sale.Id, cancellationToken);

        var consolidations = await db.SaleConsolidations
            .AsNoTracking()
            .Where(consolidation => consolidationIds.Contains(consolidation.Id))
            .Select(consolidation => new { consolidation.Id, consolidation.ConsolidationDate, consolidation.SaleCount })
            .ToDictionaryAsync(consolidation => consolidation.Id, cancellationToken);

        var reservations = await db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservationIds.Contains(reservation.Id))
            .Select(reservation => new { reservation.Id, reservation.ExternalReferenceId, reservation.CreatedAt, reservation.PaymentMethod })
            .ToDictionaryAsync(reservation => reservation.Id, cancellationToken);

        var lineDtos = lines.Select(line =>
        {
            if (line.DesktopSaleId is { } saleId && sales.TryGetValue(saleId, out var sale))
            {
                return Line(line, SourceName(sale.SourceSystem), saleId, sale.ExternalReferenceId, sale.DocDate, sale.PaymentMethod);
            }

            if (line.SaleConsolidationId is { } consolidationId && consolidations.TryGetValue(consolidationId, out var consolidation))
            {
                return Line(line, "Consolidated day", null, $"{consolidation.SaleCount} sale(s)", consolidation.ConsolidationDate, null);
            }

            if (line.StockReservationId is { } reservationId && reservations.TryGetValue(reservationId, out var reservation))
            {
                return Line(line, "Van (online)", null, reservation.ExternalReferenceId, reservation.CreatedAt, reservation.PaymentMethod);
            }

            return Line(line, "Unknown", line.DesktopSaleId, null, null, null);
        }).ToList();

        return new DailyIncomingPaymentDetailDto(DailyIncomingPaymentProjection.ToDto(header), lineDtos);
    }

    private static DailyIncomingPaymentLineDto Line(
        Models.Entities.DailyIncomingPaymentLineEntity line,
        string source,
        int? saleId,
        string? reference,
        DateTime? date,
        string? paymentMethod) =>
        new(
            line.InvoiceDocEntry,
            line.InvoiceDocNum,
            source,
            saleId,
            reference,
            date,
            paymentMethod,
            line.CashAmount,
            line.TransferAmount + line.CreditAmount);

    private static string SourceName(string? sourceSystem) => sourceSystem switch
    {
        Common.Sales.SaleSourceSystems.ShopTill => "Shop till",
        Common.Sales.SaleSourceSystems.Vending => "Vending",
        Common.Sales.SaleSourceSystems.VanSales => "Van",
        _ => sourceSystem ?? "Sale"
    };
}
