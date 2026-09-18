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
    int PageSize = 50,
    string? Channel = null
) : IRequest<ErrorOr<VanSalesInvoicesResult>>;

public sealed record VanSalesInvoicesResult(
    DateTime FromDate,
    DateTime ToDate,
    int Page,
    int PageSize,
    int TotalCount,
    VanSalesInvoiceCounts Counts,
    List<VanSalesInvoiceRow> Rows,
    List<VanSalesRepOption> Reps,
    VanSalesInvoiceSummary Summary);

/// <summary>
/// What the period's invoices add up to, over the same rows as <see cref="VanSalesInvoiceCounts"/>: after rep,
/// search and channel, before the state filter.
/// </summary>
/// <remarks>
/// <para><c>Online</c>, <c>Offline</c>: counted before the channel filter, so each says what choosing that
/// channel would show.</para>
/// <para><c>Totals</c>: one per currency. Money in two currencies is never summed into one figure.</para>
/// <para><c>NotInSap</c>, <c>NotInSapByVan</c>: invoices with no SAP number yet — sold, and not yet in the
/// back office — by currency, and the vans carrying most of that value.</para>
/// </remarks>
public sealed record VanSalesInvoiceSummary(
    int Online,
    int Offline,
    List<VanSalesMoneyTotal> Totals,
    int NotInSapCount,
    List<VanSalesMoneyTotal> NotInSap,
    List<VanSalesVanBacklog> NotInSapByVan);

/// <summary>A sum of documents in one currency.</summary>
/// <remarks>
/// <para><c>Vat</c>: the VAT the documents carry where it is known. An online sale from before receipts were
/// stored carries none.</para>
/// <para><c>NetOnlyCount</c>: how many of <c>Count</c> are in <c>Amount</c> without their VAT — see
/// <see cref="VanSalesInvoiceRow.AmountIncludesVat"/>. Zero means <c>Amount</c> is gross throughout.</para>
/// </remarks>
public sealed record VanSalesMoneyTotal(string Currency, decimal Amount, decimal Vat, int Count, int NetOnlyCount);

/// <summary>What one van has sold that SAP has not invoiced yet, in one currency.</summary>
/// <remarks><para><c>RepName</c>: the rep, when every one of those sales was made by the same one.</para></remarks>
public sealed record VanSalesVanBacklog(string WarehouseCode, string? RepName, string Currency, decimal Amount, int Count);

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
/// <para><c>SaleNumber</c>: The short number the sale is known by in the console, <c>INV10427</c> — the same
/// number Desktop Sales shows for it, because van sales are rows of the same table. Null for an online sale from
/// before receipts were stored, which has no sale row to be numbered by; it is known by its reference alone.
/// See <see cref="Common.Sales.DesktopSaleNumber"/>.</para>
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
    string? Problem,
    string? SaleNumber = null);

public sealed record VanSalesRepOption(Guid UserId, string Name);
