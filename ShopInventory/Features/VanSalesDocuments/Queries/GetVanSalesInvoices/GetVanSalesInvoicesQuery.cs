using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoices;

/// <summary>
/// Every invoice the van sales app created in a period, with where each stands at ZIMRA and in SAP.
/// </summary>
/// <remarks>
/// <para><b>Read from this database, not from SAP.</b> The portal's older van invoice list filtered SAP on
/// <c>U_Van_saleorder</c> being set, and that field is written by web invoices, consolidations, desktop and
/// till sales too — so it listed far more than the app made, and nothing that SAP had not yet taken. The
/// records that say a sale came from a handset are here: the reservations the online sales and converted
/// orders post from, and the offline sales the handset uploads.</para>
///
/// <para>A reservation is listed only once it became a sale: confirmed, fiscalised, or queued to be. A basket
/// held for a sale that was then refused, abandoned or expired never was one.</para>
/// </remarks>
public sealed record GetVanSalesInvoicesQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? RepUserId = null,
    string? State = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 50
) : IRequest<ErrorOr<VanSalesInvoicesResult>>;

public sealed record VanSalesInvoicesResult(
    DateTime FromDate,
    DateTime ToDate,
    int Page,
    int PageSize,
    int TotalCount,
    VanSalesInvoiceCounts Counts,
    List<VanSalesInvoiceRow> Rows,
    List<VanSalesRepOption> Reps);

/// <summary>How many of the period's invoices are in each state, before the state filter is applied.</summary>
public sealed record VanSalesInvoiceCounts(
    int All,
    int Complete,
    int AwaitingSap,
    int NotFiscalised,
    int InProgress,
    int NeedsAttention);

/// <summary>One invoice the app created.</summary>
/// <remarks>
/// <para><c>Reference</c>: The van order: the handset's idempotency key, <c>U_Van_saleorder</c> in SAP, and the
/// number a receipt fiscalised before SAP is filed under.</para>
/// <para><c>Channel</c>: <c>Online</c> when it posted from a reservation, <c>Offline</c> when the handset
/// uploaded it after selling without signal.</para>
/// <para><c>Amount</c>: The document total.</para>
/// <para><c>AmountIncludesVat</c>: Whether <paramref name="Amount"/> carries the tax. A sale with a receipt row
/// is known gross; an online sale from before receipts were stored carries only the net figure its reservation
/// held.</para>
/// <para><c>State</c>: One of <see cref="VanSalesDocumentStates"/>.</para>
/// <para><c>Problem</c>: What went wrong, when <paramref name="State"/> is not complete.</para>
/// </remarks>
public sealed record VanSalesInvoiceRow(
    string Reference,
    string Channel,
    DateTime TradingDate,
    DateTime CreatedAtUtc,
    Guid? RepUserId,
    string? RepName,
    string? WarehouseCode,
    string? CustomerCode,
    string? CustomerName,
    string? PaymentMethod,
    decimal Amount,
    decimal? VatAmount,
    bool AmountIncludesVat,
    string Currency,
    int? SapDocEntry,
    int? SapDocNum,
    string? FiscalReceiptNumber,
    string? FiscalVerificationCode,
    string? FiscalDay,
    string? FiscalDeviceSerial,
    string State,
    string? Problem);

public sealed record VanSalesRepOption(Guid UserId, string Name);
