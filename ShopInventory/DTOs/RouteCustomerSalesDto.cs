namespace ShopInventory.DTOs;

/// <summary>
/// Where a route customer's sale was recorded. The three van-sales write paths land in three different
/// tables, and a reader has to be told which one a row came from — the fields that are populated, and
/// what "posted" means, differ between them.
/// </summary>
public enum RouteCustomerSaleSource
{
    /// <summary>
    /// Signed on the handset and uploaded in a batch. Carries the ZIMRA receipt; reaches SAP at end of
    /// day as its own invoice.
    /// </summary>
    OfflineVanSale = 0,

    /// <summary>
    /// Sold while the handset had signal, so it posted to SAP immediately. The confirmed stock
    /// reservation is its only local record, which is why it has no receipt fields of its own.
    /// </summary>
    OnlineInvoice = 1
}

/// <summary>
/// Money for one currency.
///
/// Sales are never totalled across currencies. A van can trade in USD and ZWG on the same day, and one
/// summed figure over both is not a smaller truth — it is a wrong number. Every total in this file is
/// therefore a list of these, one per currency actually traded.
/// </summary>
public sealed class RouteCustomerSalesTotalsDto
{
    public string Currency { get; set; } = string.Empty;

    public int SaleCount { get; set; }

    /// <summary>Tax-inclusive value, as the customer paid it.</summary>
    public decimal Gross { get; set; }

    /// <summary>
    /// Tax, where the source knows it. An offline van sale carries its own split; an online sale's tax
    /// lives on the SAP invoice and contributes nothing here.
    ///
    /// There is deliberately no <c>Net</c> alongside this. <c>Gross - Vat</c> is only net where every
    /// sale in the total came from the first kind, and a property that is right for one mix and silently
    /// wrong for another is worse than the subtraction being written out where it is meant.
    /// </summary>
    public decimal Vat { get; set; }

    public decimal AmountPaid { get; set; }
}

public sealed class RouteCustomerSaleLineDto
{
    public int LineNum { get; set; }

    public string ItemCode { get; set; } = string.Empty;

    public string? ItemDescription { get; set; }

    public decimal Quantity { get; set; }

    public string? UoMCode { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal LineTotal { get; set; }

    public string? WarehouseCode { get; set; }
}

/// <summary>One sale made to a route customer, from whichever of the two tables holds it.</summary>
public sealed class RouteCustomerSaleDto
{
    public RouteCustomerSaleSource Source { get; set; }

    /// <summary>The handset's own reference — <c>van_order</c> — which is the key everything joins on.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>The trading day, in the van's own clock. Not the upload time.</summary>
    public DateTime SoldAt { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal Total { get; set; }

    public decimal VatAmount { get; set; }

    public decimal AmountPaid { get; set; }

    public string? PaymentMethod { get; set; }

    public string? PaymentReference { get; set; }

    /// <summary>Where the sale is in its journey to SAP, in words the page can print as-is.</summary>
    public string Status { get; set; } = string.Empty;

    public int? SapDocNum { get; set; }

    public int? SapDocEntry { get; set; }

    public DateTime? PostedAt { get; set; }

    // --- Fiscal, on an offline van sale. The receipt the customer is holding. ---

    public int? ReceiptGlobalNo { get; set; }

    public string? FiscalDayNo { get; set; }

    public string? FiscalVerificationCode { get; set; }

    public string? FiscalDeviceNumber { get; set; }

    public string? WarehouseCode { get; set; }

    public List<RouteCustomerSaleLineDto> Lines { get; set; } = [];
}

/// <summary>
/// An order the van took but has not yet turned into a sale.
///
/// Kept apart from the sales and out of every total on purpose: a mobile order's lines are priced after
/// capture, so its value is 0.00 until then, and folding that into a sales figure would quietly
/// understate the customer. What it does answer is what a shop has asked for and not yet received.
/// </summary>
public sealed class RouteCustomerOrderDto
{
    public int Id { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public DateTime OrderDate { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? Currency { get; set; }

    public decimal DocTotal { get; set; }

    public int LineCount { get; set; }

    /// <summary>True once the order has become an invoice, locally or in SAP.</summary>
    public bool IsInvoiced { get; set; }

    public int? SapDocNum { get; set; }

    /// <summary>
    /// The order is still waiting to be priced, so <see cref="DocTotal"/> is not a real zero. The page
    /// says so rather than printing 0.00 next to a real one.
    /// </summary>
    public bool IsAwaitingPricing { get; set; }
}

/// <summary>What a customer buys, one row per item.</summary>
public sealed class RouteCustomerProductMixRowDto
{
    public string ItemCode { get; set; } = string.Empty;

    public string? ItemDescription { get; set; }

    /// <summary>
    /// Safe to sum here, and only here: every row is one item, so the unit of measure is consistent
    /// within it. Summing across rows would add cases to bottles.
    /// </summary>
    public decimal Quantity { get; set; }

    public string? UoMCode { get; set; }

    public int LineCount { get; set; }

    /// <summary>How many distinct route customers bought it. Always 1 on a single customer's drill-down.</summary>
    public int CustomerCount { get; set; }

    public DateTime? LastSoldAt { get; set; }

    public List<RouteCustomerSalesTotalsDto> TotalsByCurrency { get; set; } = [];
}

/// <summary>One route customer's trading, as the summary and dormancy reports read it.</summary>
public sealed class RouteCustomerSalesRowDto
{
    /// <summary>Null once the customer record has been deleted; the code and name below survive it.</summary>
    public int? RouteCustomerId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string AssignedBusinessPartnerCode { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? CapturedAt { get; set; }

    public string? Phone { get; set; }

    public int SaleCount { get; set; }

    /// <summary>
    /// Lines sold, not units. Units summed across different items would add cases to bottles; the line
    /// count at least says how much was bought without pretending to a unit it does not have.
    /// </summary>
    public int LineCount { get; set; }

    public DateTime? FirstSaleAt { get; set; }

    public DateTime? LastSaleAt { get; set; }

    /// <summary>Null when the customer has never bought, which is a different thing from a long gap.</summary>
    public int? DaysSinceLastSale { get; set; }

    public int OpenOrderCount { get; set; }

    public List<RouteCustomerSalesTotalsDto> TotalsByCurrency { get; set; } = [];
}

/// <summary>One route's customers, and the route's own totals.</summary>
public sealed class RouteSalesGroupDto
{
    public string AssignedBusinessPartnerCode { get; set; } = string.Empty;

    public int CustomerCount { get; set; }

    public int SaleCount { get; set; }

    /// <summary>Customers with no sale inside the window at all.</summary>
    public int NeverBoughtCount { get; set; }

    /// <summary>Customers whose last sale is older than the dormancy threshold.</summary>
    public int DormantCount { get; set; }

    public List<RouteCustomerSalesTotalsDto> TotalsByCurrency { get; set; } = [];

    public List<RouteCustomerSalesRowDto> Customers { get; set; } = [];
}

public sealed class RouteCustomerSalesSummaryDto
{
    public DateTime From { get; set; }

    public DateTime To { get; set; }

    /// <summary>Days without a sale after which a customer counts as dormant.</summary>
    public int DormantDays { get; set; }

    public List<RouteSalesGroupDto> Routes { get; set; } = [];
}

/// <summary>Everything the drill-down page needs for one route customer.</summary>
public sealed class RouteCustomerSalesDetailDto
{
    public RouteCustomerDto Customer { get; set; } = new();

    public DateTime From { get; set; }

    public DateTime To { get; set; }

    public int SaleCount { get; set; }

    public int LineCount { get; set; }

    public DateTime? FirstSaleAt { get; set; }

    public DateTime? LastSaleAt { get; set; }

    public int? DaysSinceLastSale { get; set; }

    public List<RouteCustomerSalesTotalsDto> TotalsByCurrency { get; set; } = [];

    public List<RouteCustomerSaleDto> Sales { get; set; } = [];

    public List<RouteCustomerOrderDto> Orders { get; set; } = [];

    public List<RouteCustomerProductMixRowDto> ProductMix { get; set; } = [];
}

/// <summary>The product mix report, across one route or all of them.</summary>
public sealed class RouteCustomerProductMixDto
{
    public DateTime From { get; set; }

    public DateTime To { get; set; }

    public string? AssignedBusinessPartnerCode { get; set; }

    public int? RouteCustomerId { get; set; }

    public List<RouteCustomerProductMixRowDto> Items { get; set; } = [];
}
