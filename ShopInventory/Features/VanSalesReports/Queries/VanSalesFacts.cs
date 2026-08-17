using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Queries;

/// <summary>
/// Which of the two tables a van sale came out of.
///
/// There is no single table holding them all, and the difference is not cosmetic: an offline sale
/// carries its own tender and fiscal detail, while an online one carries neither because the money
/// side of it lives on the SAP invoice.
/// </summary>
public enum VanSaleSource
{
    /// <summary>Signed on the handset and uploaded as a batch, landing in <c>DesktopSales</c>.</summary>
    OfflineBatch = 0,

    /// <summary>
    /// Posted straight to SAP while the handset had signal. No <c>DesktopSale</c> is written, so the
    /// confirmed <c>StockReservation</c> is the only local record of it.
    /// </summary>
    OnlineInvoice = 1
}

/// <summary>
/// Which column of the departure sheet a tender belongs in.
///
/// Deliberately matched on the brand the handset sends rather than on a fiscal money type, because
/// ZIMRA has no notion of Ecocash or Innbucks — both are <c>MobileWallet</c> on the receipt, and
/// telling them apart is exactly what the cash split is for.
/// </summary>
public enum VanSalesTender
{
    Cash,
    Ecocash,
    Innbucks,

    /// <summary>
    /// Anything else, including a sale that names no tender at all. Handsets built before the payment
    /// picker send nothing, and those are reported as unallocated rather than assumed to be cash.
    /// </summary>
    Other
}

/// <summary>
/// A rep's trading day — the grain every van sales report shares.
///
/// The date is always a bare CAT day, never an instant, because that is what the business means by
/// "a day out" and what the handset states when it opens one.
/// </summary>
public readonly record struct VanSalesDayKey(Guid UserId, DateTime TradingDate);

/// <summary>
/// Identifies a shop across the whole estate.
///
/// The account is part of the key, and has to be: a route customer's code is unique only
/// <em>within</em> the van's business partner — the database says so with a unique index on
/// <c>(AssignedBusinessPartnerCode, Code)</c> and on nothing narrower. Grouping on the bare code
/// silently merges two different shops in two different territories into one row, and the merged
/// figure looks entirely plausible.
/// </summary>
public readonly record struct VanSalesOutletKey(string VanAccountCode, string OutletCode);

/// <summary>
/// One van sale, normalised across both tables it can land in, at document grain.
///
/// <paramref name="RouteCustomerCode"/> is the shop. It is nullable and often is null on older
/// history: the account on the document is the van's own, so a sale without this cannot be attributed
/// to a shop at all and must be reported as unattributed rather than folded into one.
/// </summary>
/// <param name="VanAccountCode">
/// The SAP business partner the van bills as — the document's own <c>CardCode</c>. Never the buyer;
/// it is the same on every sale a given van makes. It is here because it is half of the shop's key.
/// </param>
/// <param name="RouteCustomerId">
/// The shop's row id where the record still exists, which is the exact identity. Null on sales whose
/// customer record was never resolved; the code and name snapshots survive either way.
/// </param>
public sealed record VanSaleFact(
    Guid UserId,
    DateTime TradingDate,
    VanSaleSource Source,
    string ExternalReferenceId,
    string VanAccountCode,
    int? RouteCustomerId,
    string? RouteCustomerCode,
    string? RouteCustomerName,
    string? PaymentMethod,
    decimal TotalAmount,
    string? Currency,
    string? WarehouseCode)
{
    /// <summary>Which column of the cash split this sale's takings belong in.</summary>
    public VanSalesTender Tender => VanSalesFacts.ClassifyTender(PaymentMethod);

    public VanSalesDayKey Key => new(UserId, TradingDate);

    /// <summary>The shop this sale is attributable to, or null when none was recorded.</summary>
    public VanSalesOutletKey? Outlet => RouteCustomerCode is null
        ? null
        : new VanSalesOutletKey(VanAccountCode, RouteCustomerCode);
}

/// <summary>
/// One line of one van sale, normalised across both tables.
///
/// Two quantities, because the two tables do not agree on what they store and collapsing them would
/// silently add cases to eaches:
///
/// - <see cref="Quantity"/> is what was sold, in <see cref="UoMCode"/> — the unit the rep priced in.
///   This is the one to report and the one that pairs with <see cref="UnitPrice"/>.
/// - <see cref="InventoryQuantity"/> is the same line converted to the inventory unit, which only the
///   online path records. It is null on every offline sale, so stock reconciliation must handle its
///   absence rather than treat null as zero.
///
/// Never sum <see cref="Quantity"/> across items. Mixed units of measure make that total meaningless;
/// count items, or convert through the volume factors first.
/// </summary>
public sealed record VanSaleLineFact(
    Guid UserId,
    DateTime TradingDate,
    VanSaleSource Source,
    string ExternalReferenceId,
    string VanAccountCode,
    int? RouteCustomerId,
    string? RouteCustomerCode,
    string? RouteCustomerName,
    int LineNum,
    string ItemCode,
    string? ItemDescription,
    decimal Quantity,
    decimal? InventoryQuantity,
    string? UoMCode,
    decimal UnitPrice,
    decimal LineTotal,
    decimal DiscountPercent,
    string? WarehouseCode,
    string? Currency,
    string? PaymentMethod)
{
    public VanSalesDayKey Key => new(UserId, TradingDate);

    /// <summary>The shop this line's sale is attributable to, or null when none was recorded.</summary>
    public VanSalesOutletKey? Outlet => RouteCustomerCode is null
        ? null
        : new VanSalesOutletKey(VanAccountCode, RouteCustomerCode);
}

/// <summary>
/// What slice of van sales history to read. Dates are bare CAT trading days, inclusive at both ends.
/// </summary>
/// <param name="FromDate">First trading day to include.</param>
/// <param name="ToDate">Last trading day to include.</param>
/// <param name="UserId">One rep, or every rep when null.</param>
public sealed record VanSalesFactFilter(DateTime FromDate, DateTime ToDate, Guid? UserId = null)
{
    public DateTime From => FromDate.Date;

    public DateTime To => ToDate.Date;
}

/// <summary>
/// The primitives every van sales report shares: the clock conversion, the rep attribution and the
/// tender classification.
///
/// These live in one place because each of them has a wrong answer that produces no error. A day
/// window applied to the wrong clock files every evening sale a day early; an unparsed
/// <c>CreatedBy</c> silently drops a rep's whole day; and a tender that falls through to cash makes a
/// declaration variance disappear. Solving any of them twice invites the two answers to disagree.
/// </summary>
public static class VanSalesFacts
{
    /// <summary>
    /// The widest period any van sales report will answer for.
    ///
    /// Not a performance guard — these reads are all local. It stops a mistyped year turning one page
    /// load into a decade of history.
    /// </summary>
    public const int MaximumDays = 400;

    /// <summary>
    /// The half-open UTC interval a CAT day range covers, for the tables that store instants.
    ///
    /// Half-open on purpose: CAT is UTC+2, so the last moment of a trading day is 21:59:59.999… UTC,
    /// and an inclusive upper bound would need a sentinel precision that the two databases do not
    /// agree on. Compare with <c>&gt;= FromUtc</c> and <c>&lt; ToUtcExclusive</c>.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtcExclusive) ToUtcWindow(DateTime fromCat, DateTime toCat) =>
        (AuditService.FromCAT(fromCat.Date), AuditService.FromCAT(toCat.Date.AddDays(1)));

    /// <summary>
    /// The CAT trading day a UTC instant falls in.
    ///
    /// A sale made at 22:30 CAT is 20:30 UTC on the same date, but one made at 00:30 CAT is 22:30 UTC
    /// on the date before — which is why this exists rather than reading <c>.Date</c> off the instant.
    /// </summary>
    public static DateTime TradingDayOf(DateTime utcInstant) => AuditService.ToCAT(utcInstant).Date;

    /// <summary>
    /// Resolves the rep who made a sale.
    ///
    /// <c>CreatedBy</c> holds the user's id as a string on both sale tables. Anything else is a sale
    /// from another system that slipped past the source filter; it is skipped rather than guessed at,
    /// because a sale credited to the wrong rep is worse than one credited to none.
    /// </summary>
    public static bool TryResolveRep(string? createdBy, out Guid userId)
    {
        if (string.IsNullOrWhiteSpace(createdBy))
        {
            userId = Guid.Empty;
            return false;
        }

        return Guid.TryParse(createdBy, out userId);
    }

    /// <summary>
    /// Which column of the cash split a tender belongs in. See <see cref="VanSalesTender"/> for why
    /// this matches on brand names rather than on a fiscal money type.
    /// </summary>
    public static VanSalesTender ClassifyTender(string? paymentMethod)
    {
        if (string.IsNullOrWhiteSpace(paymentMethod))
        {
            return VanSalesTender.Other;
        }

        var normalised = paymentMethod.Trim();

        if (normalised.Equals("Cash", StringComparison.OrdinalIgnoreCase))
        {
            return VanSalesTender.Cash;
        }

        if (normalised.Contains("ecocash", StringComparison.OrdinalIgnoreCase))
        {
            return VanSalesTender.Ecocash;
        }

        // "inbucks" as well as "innbucks": both spellings are in the field on handsets that are still
        // uploading, and the one-n build is not going to be recalled.
        return normalised.Contains("innbucks", StringComparison.OrdinalIgnoreCase)
               || normalised.Contains("inbucks", StringComparison.OrdinalIgnoreCase)
            ? VanSalesTender.Innbucks
            : VanSalesTender.Other;
    }
}
