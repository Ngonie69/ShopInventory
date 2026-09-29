using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSaleByVanOrder;

/// <summary>
/// Reads the sale row a van's <c>POST order</c> wrote, by the <c>van_order</c> it was posted under.
/// </summary>
/// <remarks>
/// <para><b>The sale row, not SAP.</b> Both online paths write a <c>DesktopSales</c> row under the van order —
/// <see cref="Services.VanSaleFiscalFirstPoster"/> before it asks the device to sign, and the stamped path
/// once SAP has the invoice — and the queue fills in its SAP numbers when it posts. So a sale signed and not
/// yet posted is answered here with its receipt, where invoice history, which reads SAP, has nothing.</para>
///
/// <para><b>Scope.</b> A sale is visible to the account that made it, or to any account whose customer scope
/// holds the code the sale bills. The second half is what lets a second rep on the same van check a sale
/// the first one made — an ordinary day — and it is the same scope invoice history is narrowed by, so this
/// shows nothing that screen would not. A sale outside both answers exactly as a sale that does not exist:
/// saying "found, but not yours" would confirm another van's reference to anyone who guessed it.</para>
/// </remarks>
public sealed class GetVanSalesSaleByVanOrderHandler(
    ApplicationDbContext db,
    ILogger<GetVanSalesSaleByVanOrderHandler> logger
) : IRequestHandler<GetVanSalesSaleByVanOrderQuery, ErrorOr<VanSalesSaleLookupResponse>>
{
    /// <summary>
    /// The width of <see cref="DesktopSaleEntity.ExternalReferenceId"/>. Anything longer names no row, and is
    /// answered as not found rather than refused — the handset reads anything but a 200 as "could not check".
    /// </summary>
    private const int MaxVanOrderLength = 100;

    public async Task<ErrorOr<VanSalesSaleLookupResponse>> Handle(
        GetVanSalesSaleByVanOrderQuery query,
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

        // One row at most — ExternalReferenceId is unique — and only a van sale's. A till or vending sale is
        // never posted under a van order, and this route has no business confirming one exists.
        var sale = await db.DesktopSales
            .AsNoTracking()
            .Where(candidate => candidate.ExternalReferenceId == vanOrder
                && SaleSourceSystems.VanSaleSources.Contains(candidate.SourceSystem))
            .Select(candidate => new SaleRow
            {
                Id = candidate.Id,
                CardCode = candidate.CardCode,
                CardName = candidate.CardName,
                CreatedBy = candidate.CreatedBy,
                CreatedAt = candidate.CreatedAt,
                TotalAmount = candidate.TotalAmount,
                Currency = candidate.Currency,
                FiscalizationStatus = candidate.FiscalizationStatus,
                FiscalVerificationCode = candidate.FiscalVerificationCode,
                FiscalQRCode = candidate.FiscalQRCode,
                FiscalDayNo = candidate.FiscalDayNo,
                FiscalDeviceNumber = candidate.FiscalDeviceNumber,
                FiscalReceiptNumber = candidate.FiscalReceiptNumber,
                ReceiptGlobalNo = candidate.ReceiptGlobalNo,
                SapDocNum = candidate.SapDocNum,
                SapDocEntry = candidate.SapDocEntry
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (sale is null)
        {
            return NotFound(vanOrder);
        }

        if (!MadeBy(sale, user.Id))
        {
            var scope = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
                db,
                user,
                logger,
                cancellationToken);

            if (!scope.Contains(sale.CardCode.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "User {UserId} asked for van sale {VanOrder}, which bills {CardCode} outside their scope; answered as not found",
                    user.Id,
                    vanOrder,
                    sale.CardCode);

                return NotFound(vanOrder);
            }
        }

        return new VanSalesSaleLookupResponse
        {
            Found = true,
            VanOrder = vanOrder,
            SaleNumber = DesktopSaleNumber.Format(sale.Id),

            // What the post itself counts as fiscalised — a Success status, which is the only way its
            // fiscal-first path answers with a receipt — and a verification code to print, without which
            // there is no slip to hand over.
            Fiscalised = sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success
                && !string.IsNullOrWhiteSpace(sale.FiscalVerificationCode),
            FiscalStatus = sale.FiscalizationStatus.ToString(),
            VerificationCode = sale.FiscalVerificationCode,
            QrCode = sale.FiscalQRCode,
            FiscalDay = sale.FiscalDayNo,
            DeviceSerial = sale.FiscalDeviceNumber,

            // A handset-stamped receipt carries the number as a column; one the office signed carries only
            // the device's text. The same precedence invoice history uses.
            ReceiptGlobalNo = sale.ReceiptGlobalNo ?? ParseReceiptNumber(sale.FiscalReceiptNumber),
            SapDocNum = sale.SapDocNum,
            SapDocEntry = sale.SapDocEntry,
            CardCode = sale.CardCode,
            CardName = sale.CardName,
            Total = sale.TotalAmount,
            Currency = sale.Currency,

            // UTC as written. Stated as such, because a provider that reads it back unzoned would otherwise
            // serialise it with no offset and the handset would take it for local time.
            CreatedAtUtc = DateTime.SpecifyKind(sale.CreatedAt, DateTimeKind.Utc)
        };
    }

    /// <summary>
    /// Whether this account made the sale. Both online paths write the poster's user id into
    /// <c>CreatedBy</c>; parsed rather than compared as text so its casing cannot matter.
    /// </summary>
    private static bool MadeBy(SaleRow sale, Guid userId) =>
        Guid.TryParse(sale.CreatedBy, out var createdBy) && createdBy == userId;

    private static int? ParseReceiptNumber(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;

    private static VanSalesSaleLookupResponse NotFound(string vanOrder) => new()
    {
        Found = false,
        VanOrder = vanOrder
    };

    private sealed class SaleRow
    {
        public int Id { get; init; }
        public string CardCode { get; init; } = string.Empty;
        public string? CardName { get; init; }
        public string? CreatedBy { get; init; }
        public DateTime CreatedAt { get; init; }
        public decimal TotalAmount { get; init; }
        public string? Currency { get; init; }
        public DesktopSaleFiscalizationStatus FiscalizationStatus { get; init; }
        public string? FiscalVerificationCode { get; init; }
        public string? FiscalQRCode { get; init; }
        public string? FiscalDayNo { get; init; }
        public string? FiscalDeviceNumber { get; init; }
        public string? FiscalReceiptNumber { get; init; }
        public int? ReceiptGlobalNo { get; init; }
        public int? SapDocNum { get; init; }
        public int? SapDocEntry { get; init; }
    }
}
