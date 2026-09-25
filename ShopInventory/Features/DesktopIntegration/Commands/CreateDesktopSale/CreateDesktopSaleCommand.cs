using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;

/// <summary>
/// Creates a local desktop sale, validates against stock snapshot, and fiscalizes immediately.
/// </summary>
/// <param name="Request">The basket, tender and document details the till submitted.</param>
/// <param name="UserId">
/// The signed-in account. The sale's customer, warehouse and cost centre are read from it rather than
/// from <paramref name="Request"/>, so this is the identity the whole sale hangs off — not just an
/// audit stamp.
/// </param>
public sealed record CreateDesktopSaleCommand(
    CreateDesktopSaleRequest Request,
    Guid UserId
) : IRequest<ErrorOr<CreateDesktopSaleResult>>;

/// <summary>
/// A sale, and whether this request is what made it.
/// </summary>
/// <param name="Sale">The sale, identical in either case.</param>
/// <param name="WasExisting">
/// True when the reference already had a sale and this request was answered with it.
/// </param>
/// <remarks>
/// The flag exists so the endpoint can answer <c>200 OK</c> for a replay and keep <c>201 Created</c>
/// for a creation. Both used to be 201, which left a client no way — even in principle — to tell a
/// sale it had just made from one it was merely being shown. A till took the second for the first,
/// printed a receipt for it, banked the takings against it and deducted the stock.
///
/// <para>
/// It is kept off <see cref="DesktopSaleResponseDto"/> deliberately. The body describes the sale, and
/// the sale is the same object however the caller arrived at it; whether this particular request
/// created it is a property of the exchange, which is what the status line is for.
/// </para>
/// </remarks>
public sealed record CreateDesktopSaleResult(DesktopSaleResponseDto Sale, bool WasExisting);

/// <summary>
/// Request DTO for creating a desktop sale.
/// </summary>
public class CreateDesktopSaleRequest
{
    public string? ExternalReferenceId { get; set; }
    public string? SourceSystem { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public string? DocDate { get; set; }

    /// <summary>
    /// The day to post the sale's SAP invoice under, <c>yyyy-MM-dd</c>, when the operator chose one.
    /// Blank, or today, posts on the day of sale.
    /// </summary>
    /// <remarks>
    /// Only honoured while an admin has custom posting dates switched on; otherwise a date other than
    /// today is refused rather than quietly replaced, because the operator picked it on purpose. It moves
    /// SAP's posting, due and document dates and nothing else — see <c>DesktopSaleEntity.PostingDate</c>.
    /// Left out of the JSON when null so the request's idempotency hash is what it was before the field
    /// existed: a retry of a sale made before it shipped still matches.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PostingDate { get; set; }
    public int? SalesPersonCode { get; set; }
    public string? NumAtCard { get; set; }
    public string? Comments { get; set; }
    public string? DocCurrency { get; set; }
    public bool Fiscalize { get; set; } = true;
    public string WarehouseCode { get; set; } = string.Empty;
    public string? PaymentMethod { get; set; }
    public string? PaymentReference { get; set; }

    /// <summary>
    /// The vendor being invoiced, for vending. Required there and ignored elsewhere.
    /// </summary>
    /// <remarks>
    /// A code, not an id: it is what the operator picks and what an administrator manages, and it is
    /// resolved server-side against the vendors assigned to this account's business partner. Naming a
    /// vendor that is not on that list — or one that has been deactivated — is refused, which is what
    /// makes deactivating a vendor code actually stop it trading.
    /// </remarks>
    public string? VendorCode { get; set; }
    public decimal AmountPaid { get; set; }
    public List<CreateDesktopSaleLineRequest> Lines { get; set; } = new();
}

public class CreateDesktopSaleLineRequest
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string? TaxCode { get; set; }
    public decimal DiscountPercent { get; set; }
    public string? UoMCode { get; set; }
}

/// <summary>
/// Response DTO for a created desktop sale.
/// </summary>
public class DesktopSaleResponseDto
{
    public int SaleId { get; set; }

    /// <summary>
    /// The short number this sale is known by, formatted — <c>INV10427</c>.
    /// </summary>
    /// <remarks>
    /// This is the number the customer's receipt already carries: the KefShop till prints
    /// <c>INV{saleId}</c> from <see cref="SaleId"/> below, and it is what the customer reads back when
    /// they return goods. It is sent formatted so that a client printing it has no format of its own to
    /// get wrong — the console searches for this exact string, and a receipt spelling it differently
    /// would be a receipt the console cannot find.
    /// See <see cref="Common.Sales.DesktopSaleNumber"/>.
    /// </remarks>
    public string SaleNumber { get; set; } = string.Empty;

    public string ExternalReferenceId { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;

    /// <summary>
    /// The warehouse the sale actually drew from, which is the account's rather than anything the
    /// request asked for. Returned so a caller can see what it sold from without inferring it.
    /// </summary>
    public string WarehouseCode { get; set; } = string.Empty;

    public decimal TotalAmount { get; set; }
    public decimal VatAmount { get; set; }
    public string FiscalizationStatus { get; set; } = string.Empty;
    public string? FiscalReceiptNumber { get; set; }
    public string? FiscalQRCode { get; set; }
    public string? FiscalVerificationCode { get; set; }

    /// <summary>
    /// The device that took the receipt, and the fiscal day it filed it under.
    /// </summary>
    /// <remarks>
    /// Returned because the receipt prints them and a till has no other way to know either. The
    /// device is not a setting to be read from configuration: submissions may fail over between
    /// devices, and what comes back here is the one that actually signed this receipt.
    /// </remarks>
    public string? FiscalDeviceNumber { get; set; }

    /// <inheritdoc cref="FiscalDeviceNumber"/>
    public string? FiscalDayNo { get; set; }

    public string? FiscalError { get; set; }
    public DateTime CreatedAt { get; set; }
}
