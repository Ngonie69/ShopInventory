using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoice;

public sealed class GetVanSalesInvoiceHandler(ApplicationDbContext db)
    : IRequestHandler<GetVanSalesInvoiceQuery, ErrorOr<VanSalesInvoiceDetail>>
{
    public async Task<ErrorOr<VanSalesInvoiceDetail>> Handle(
        GetVanSalesInvoiceQuery request,
        CancellationToken cancellationToken)
    {
        var reference = request.Reference?.Trim();

        if (string.IsNullOrEmpty(reference))
        {
            return Error.Validation("VanSalesDocuments.MissingReference", "A van order reference is required.");
        }

        // The period is ignored when a reference is given; the dates only have to be valid.
        var records = await VanSalesInvoiceReader.LoadAsync(
            db, DateTime.UtcNow.Date, DateTime.UtcNow.Date, reference, cancellationToken);

        var record = records.FirstOrDefault();

        if (record is null)
        {
            return Error.NotFound(
                "VanSalesDocuments.InvoiceNotFound",
                $"No invoice from the van sales app has the reference {reference}.");
        }

        var lines = record.ReservationId is not null
            ? await db.StockReservationLines
                .AsNoTracking()
                .Where(l => l.Reservation.ReservationId == record.ReservationId)
                .OrderBy(l => l.LineNum)
                .Select(l => new VanSalesInvoiceLine(
                    l.LineNum,
                    l.ItemCode,
                    l.ItemDescription,
                    l.OriginalQuantity,
                    l.UoMCode,
                    l.UnitPrice,
                    l.DiscountPercent,
                    l.LineTotal,
                    l.TaxCode))
                .ToListAsync(cancellationToken)
            : await db.DesktopSaleLines
                .AsNoTracking()
                .Where(l => l.SaleId == record.DesktopSaleId)
                .OrderBy(l => l.LineNum)
                .Select(l => new VanSalesInvoiceLine(
                    l.LineNum,
                    l.ItemCode,
                    l.ItemDescription,
                    l.Quantity,
                    l.UoMCode,
                    l.UnitPrice,
                    l.DiscountPercent,
                    l.LineTotal,
                    l.TaxCode))
                .ToListAsync(cancellationToken);

        return new VanSalesInvoiceDetail(
            record.Row,
            record.FiscalQrCode,
            record.PostingAttempts,
            record.QueueStatus,
            lines);
    }
}
