using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;

/// <summary>
/// A page of captured sales.
/// </summary>
/// <remarks>
/// <c>SourceSystem</c> selects exactly one source, or null for the default scope. Null is not
/// "everything": it excludes <c>SaleSourceSystems.VanSalesOnline</c>, whose rows are receipt carriers for
/// sales already recorded as their confirmed reservation and their SAP invoice. Name that source to list
/// them.
///
/// <c>CallerUserId</c> is required and positional rather than an optional trailing parameter, because
/// it is what the handler resolves the warehouse scope from. Before it existed, <c>WarehouseCode</c>
/// came off the query string and was checked against nobody, so any authenticated staff account could
/// read any shop's takings. Making it the first parameter means a new call site cannot compile without
/// supplying one.
///
/// <c>Search</c> narrows to sales whose till reference, fiscal receipt number or route customer's code or
/// name contains the text, ignoring case, or whose sale number or SAP document number is that number —
/// a bare number is tried as both, because the two are what a person holding a printed document has. It
/// also matches the customer's own code and name, which is what an operator has when they have no paper
/// at all. It finds; it does not widen — every other filter and the caller's scope still apply.
///
/// <para>
/// <b>The four plural filters and their singular twins.</b> <c>Warehouses</c>, <c>ConsolidationStatuses</c>
/// and <c>SourceSystems</c> each stand beside a singular parameter that predates them, and a caller may
/// use either. The singular is folded into the plural before anything is filtered — see
/// <see cref="Warehouses"/> and its siblings' resolvers on the handler — so the two cannot disagree, and
/// the many existing callers that pass one string keep working unchanged. <c>FiscalizationStatuses</c> and
/// <c>PaymentMethods</c> have no singular twin because nothing ever filtered on them.
/// </para>
///
/// <para>
/// <b>Naming a source turns the default scope off.</b> That is true of <c>SourceSystems</c> exactly as it
/// was of <c>SourceSystem</c>: an empty list is the default scope, which excludes
/// <c>SaleSourceSystems.VanSalesOnline</c>, and a non-empty one is taken literally.
/// </para>
/// </remarks>
public sealed record GetDesktopSalesQuery(
    Guid CallerUserId,
    string? WarehouseCode = null,
    string? CardCode = null,
    string? ConsolidationStatus = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int Page = 1,
    int PageSize = 50,
    string? SourceSystem = null,
    string? Search = null,
    IReadOnlyList<string>? Warehouses = null,
    IReadOnlyList<string>? ConsolidationStatuses = null,
    IReadOnlyList<string>? FiscalizationStatuses = null,
    IReadOnlyList<string>? PaymentMethods = null,
    IReadOnlyList<string>? SourceSystems = null,
    decimal? MinTotal = null,
    decimal? MaxTotal = null,
    string? PaymentDifference = null,
    string? Sort = null,

    // Off unless a caller says it will draw them. Facets are five grouped counts and a second total over
    // the same window as the list — worth it for a console whose filter panel shows what each chip would
    // give, and pure waste for a till polling for its own recent sales.
    bool IncludeFacets = false
) : IRequest<ErrorOr<DesktopSalesListResult>>;

/// <summary>How a page of sales is ordered. Anything unrecognised is <see cref="Newest"/>.</summary>
/// <remarks>
/// Ordering belongs to the query rather than to the reader because the reader holds one page. A console
/// that sorted the fifty rows it was handed would be sorting a page of the answer, not the answer, and
/// "total, high to low" would name the largest sale on page three and not the largest sale.
/// </remarks>
public static class DesktopSalesSortOrders
{
    public const string Newest = "newest";
    public const string Oldest = "oldest";
    public const string TotalDescending = "total-desc";
    public const string TotalAscending = "total-asc";
    public const string Customer = "customer";
}

/// <summary>
/// Which sales to keep by how their tender compares with what they were rung up for.
/// </summary>
/// <remarks>
/// A shop till rounds cash up, so an overpaid sale is the change the customer was given and is normal. An
/// underpaid one is not: the drawer took less than the invoice says, and the two go out of a day's takings
/// in opposite directions. Anything unrecognised is <see cref="Any"/>.
/// </remarks>
public static class DesktopSalesPaymentDifferences
{
    public const string Any = "any";
    public const string Exact = "exact";
    public const string Under = "under";
    public const string Over = "over";
}

/// <summary>
/// Reserved filter values, which stand for something no stored value could.
/// </summary>
public static class DesktopSalesFilterValues
{
    /// <summary>
    /// The column is empty: a sale whose till recorded no tender, or a row written before its source
    /// was named.
    /// </summary>
    /// <remarks>
    /// A facet reports blanks under this rather than as the empty string, and the matching filter
    /// accepts it, because the two have to be the same word. A console draws its chips from the
    /// facet, so a blank reported as "" would come back as a filter value that a query string drops
    /// and an <c>IN</c> list cannot express — a chip that looked set, counted towards the badge, and
    /// narrowed nothing.
    /// </remarks>
    public const string Blank = "(blank)";
}

/// <summary>One value a filter group could be set to, and how many sales would still match if it were.</summary>
public sealed record DesktopSalesFacet(string Value, int Count);

/// <summary>
/// What each filter group would return, counted against every <i>other</i> filter in the request.
/// </summary>
/// <remarks>
/// Counted with the group's own selection lifted, which is the only count that is any use on the control
/// that sets it: a console drawing "Failed 3" beside a chip is telling the operator what pressing it would
/// give them. Counted with the selection still applied, an unpicked chip in a group where something else is
/// picked would always read 0, and every chip would look like a dead end.
///
/// The period, the caller's warehouse scope, the search and every other group still apply, so these are
/// counts within what the operator is already looking at rather than counts of the table.
/// </remarks>
public sealed record DesktopSalesFacets(
    List<DesktopSalesFacet> Consolidation,
    List<DesktopSalesFacet> Fiscalization,
    List<DesktopSalesFacet> Warehouse,
    List<DesktopSalesFacet> PaymentMethod,
    List<DesktopSalesFacet> SourceSystem
)
{
    public static DesktopSalesFacets Empty => new([], [], [], [], []);
}

public sealed record DesktopSalesListResult(
    List<DesktopSaleListItemDto> Sales,
    int TotalCount,
    int Page,
    int PageSize,
    bool HasMore,

    // What this period holds before any of the request's own filters narrowed it: the denominator behind
    // a console's "filtered from N". The caller's scope and the period and nothing else — the period is
    // what the operator chose to look at, and a number that ignored it would compare a day against the
    // whole table.
    int UnfilteredCount = 0,

    DesktopSalesFacets? Facets = null
);

public sealed record DesktopSaleListItemDto(
    int Id,

    // The short number a person names this sale by, formatted once here so the console, the receipt the
    // till printed and the search box all spell it the same way. See Common.Sales.DesktopSaleNumber.
    string SaleNumber,

    string ExternalReferenceId,
    string? SourceSystem,
    string CardCode,
    string? CardName,
    DateTime DocDate,
    decimal TotalAmount,
    decimal VatAmount,
    string Currency,
    string FiscalizationStatus,
    string? FiscalReceiptNumber,

    // The receipt the device issued, so a reprint carries the fiscal block the original did. It is
    // held here and nowhere else a caller can reach: the receipt was signed under the sale's own
    // external reference rather than a SAP document number, so the invoice this sale eventually
    // becomes is marked fiscalised without the QR ever being restated on it.
    string? FiscalQRCode,
    string? FiscalVerificationCode,
    string? FiscalDeviceNumber,
    string? FiscalDayNo,

    // What the device said when it did not sign, and how many times it has been asked. Without these a
    // failed sale could only be described as "the device could not sign this receipt", which is equally
    // true of a busy card that will sign in a minute and a line the device will refuse forever.
    string? FiscalError,
    int FiscalizationAttempts,

    string ConsolidationStatus,
    int? ConsolidationId,
    string WarehouseCode,
    string? PaymentMethod,
    string? PaymentReference,
    decimal AmountPaid,
    string? CreatedBy,

    // Who that account belongs to, resolved by SaleOperatorNames. CreatedBy above is an account id
    // and nothing else — every writer of the column stores one — so a console that showed it raw
    // showed a GUID where it meant to name a cashier. Null when the id names no account any more,
    // which the reader states in words rather than falling back to the id.
    string? CreatedByName,

    DateTime CreatedAt,

    // Who bought, when the buyer is a route customer: a van's shop or a vending depot's vendor. CardCode
    // above is then the van or the depot, not the buyer. Code and name are the snapshots the sale was
    // made under, so a renamed or deleted customer does not rewrite history; the id is null once the
    // customer is gone, and all three are null on a sale to a real SAP business partner.
    int? RouteCustomerId,
    string? RouteCustomerCode,
    string? RouteCustomerName,

    // --- Where this sale got to on its way to SAP ---
    //
    // The console offers a manual post, so it has to be able to say what there is to post and what
    // happened last time. Without these the only thing a page could show was "Awaiting close", which
    // is equally true of a sale uploaded a minute ago and one SAP has refused six times.

    // The SAP A/R invoice this sale posted as, for the one-to-one routes, and how many times it has
    // been offered to SAP so far.
    int? SapDocEntry,
    int? SapDocNum,
    DateTime? PostedAt,
    int PostingAttempts,
    string? LastPostingError,

    // How the settlement went: null (not attempted), "Posted", "PostedUnconfirmed", "Failed" or
    // "Unmapped". Reported beside the invoice because the two fail independently — an invoice can
    // post and its payment not, leaving a real open A/R document that must not be re-invoiced.
    // The payment's own document number goes with it, because chasing an unsettled invoice in SAP
    // starts with knowing whether there is a payment to look at.
    string? PaymentStatus,
    int? PaymentSapDocNum,

    // Why this sale may not be posted on request, or null when it may be. From
    // DesktopSalePostEligibility, which is the same rule the posting command refuses on — so a row
    // the console offers a button for is a row the command accepts.
    string? PostRefusal,

    // Why this sale may not be fiscalised again on request, or null when it may. From
    // DesktopSaleFiscalisationRetry, the rule the retry command refuses on.
    string? FiscaliseRefusal,

    List<DesktopSaleLineItemDto> Lines
)
{
    /// <summary>Whether the console should offer this sale a "Retry fiscalisation" button.</summary>
    public bool CanRetryFiscalisation => FiscaliseRefusal is null;

    /// <summary>
    /// Whether the console should offer this sale a "Post to SAP" button.
    /// </summary>
    /// <remarks>
    /// Derived rather than carried, so it cannot contradict the reason beside it.
    /// </remarks>
    public bool CanPostToSap => PostRefusal is null;
}

public sealed record DesktopSaleLineItemDto(
    int LineNum,
    string ItemCode,
    string? ItemDescription,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    string WarehouseCode,
    string? TaxCode,
    decimal DiscountPercent
);
