using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayments;

public sealed class GetDailyIncomingPaymentsHandler(ApplicationDbContext db)
    : IRequestHandler<GetDailyIncomingPaymentsQuery, ErrorOr<List<DailyIncomingPaymentSummaryDto>>>
{
    public async Task<ErrorOr<List<DailyIncomingPaymentSummaryDto>>> Handle(
        GetDailyIncomingPaymentsQuery request, CancellationToken cancellationToken)
    {
        var from = request.From.Date;
        var to = request.To.Date;

        var query = db.DailyIncomingPayments
            .AsNoTracking()
            .Where(payment => payment.PaymentDate >= from && payment.PaymentDate <= to);

        if (!string.IsNullOrWhiteSpace(request.CardCode))
        {
            var cardCode = request.CardCode.Trim();
            query = query.Where(payment => payment.CardCode == cardCode);
        }

        if (Enum.TryParse<DailyIncomingPaymentStatus>(request.Status, ignoreCase: true, out var status))
        {
            query = query.Where(payment => payment.Status == status);
        }

        var rows = await query
            .OrderByDescending(payment => payment.PaymentDate)
            .ThenBy(payment => payment.CardCode)
            .Select(DailyIncomingPaymentProjection.Summary)
            .ToListAsync(cancellationToken);

        return rows.Select(DailyIncomingPaymentProjection.ToDto).ToList();
    }
}
