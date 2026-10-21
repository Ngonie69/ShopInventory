using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSalesOrderByVanOrder;

/// <summary>
/// Reads the sales order a van's <c>POST sales-order</c> wrote, by the <c>van_order</c> it was sent under.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> A rep whose order lost its reply had no way to ask whether it landed, so the
/// handset could only say "Error" and leave them to send it again — or to change the basket, which mints a
/// new reference and makes a second order. With this, the handset can hold the order as "not confirmed"
/// and ask.</para>
///
/// <para><b>The key.</b> The van post stores <c>van_order</c>, trimmed, as the order's
/// <c>ClientRequestId</c>, which is unique — so there is at most one row to find.</para>
///
/// <para><b>Scope</b> is the sale lookup's (<c>GetVanSalesSaleByVanOrderHandler</c>): the account that made
/// the order, or any account whose customer scope holds the code it bills. Anything else answers exactly as
/// an order that does not exist, so a guessed reference confirms nothing.</para>
/// </remarks>
public sealed class GetVanSalesSalesOrderByVanOrderHandler(
    ApplicationDbContext db,
    ILogger<GetVanSalesSalesOrderByVanOrderHandler> logger
) : IRequestHandler<GetVanSalesSalesOrderByVanOrderQuery, ErrorOr<VanSalesSalesOrderLookupResponse>>
{
    /// <summary>
    /// The width of <c>SalesOrderEntity.ClientRequestId</c>. Anything longer names no row, and is answered as
    /// not found rather than refused — the handset reads anything but a 200 as "could not check".
    /// </summary>
    private const int MaxVanOrderLength = 200;

    public async Task<ErrorOr<VanSalesSalesOrderLookupResponse>> Handle(
        GetVanSalesSalesOrderByVanOrderQuery query,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        // Trimmed because the post trims it before writing the row.
        var vanOrder = query.VanOrder?.Trim() ?? string.Empty;

        if (vanOrder.Length == 0 || vanOrder.Length > MaxVanOrderLength)
        {
            return NotFound(vanOrder);
        }

        var order = await db.SalesOrders
            .AsNoTracking()
            .Where(candidate => candidate.ClientRequestId == vanOrder)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.OrderNumber,
                candidate.Status,
                candidate.SAPDocNum,
                candidate.CardCode,
                candidate.CardName,
                candidate.DocTotal,
                candidate.Currency,
                candidate.CreatedByUserId,
                candidate.CreatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null)
        {
            return NotFound(vanOrder);
        }

        if (order.CreatedByUserId != user.Id)
        {
            var scope = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
                db,
                user,
                logger,
                cancellationToken);

            if (!scope.Contains(order.CardCode.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "User {UserId} asked for van sales order {VanOrder}, which bills {CardCode} outside their scope; answered as not found",
                    user.Id,
                    vanOrder,
                    order.CardCode);

                return NotFound(vanOrder);
            }
        }

        return new VanSalesSalesOrderLookupResponse
        {
            Found = true,
            VanOrder = vanOrder,
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            Status = order.Status.ToString(),
            SapDocNum = order.SAPDocNum,
            CardCode = order.CardCode,
            CardName = order.CardName,
            Total = order.DocTotal,
            Currency = order.Currency,

            // UTC as written, stated as such for the reason the sale lookup gives.
            CreatedAtUtc = DateTime.SpecifyKind(order.CreatedAt, DateTimeKind.Utc)
        };
    }

    private static VanSalesSalesOrderLookupResponse NotFound(string vanOrder) => new()
    {
        Found = false,
        VanOrder = vanOrder
    };
}
