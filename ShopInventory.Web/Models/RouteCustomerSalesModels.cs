namespace ShopInventory.Web.Models;

// Hand-mirrored from ShopInventory/DTOs/RouteCustomerSalesDto.cs. Nullability has to match property for
// property: System.Text.Json throws on a null arriving into a non-nullable member, the page catches it,
// and the result reads as "no data" rather than as the deserialization failure it is.

/// <summary>
/// Which table a sale came from. Numeric on the wire — the API registers no string enum converter — so
/// these values are the contract and must not be reordered.
/// </summary>
public enum RouteCustomerSaleSource
{
    OfflineVanSale = 0,
    OnlineInvoice = 1
}

/// <summary>
/// Money for one currency. Never summed across currencies: a van that took USD 40 and ZWG 900 took no
/// single number, and the API returns one of these per currency for exactly that reason.
/// </summary>
public class RouteCustomerSalesTotalsModel
{
    public string Currency { get; set; } = string.Empty;

    public int SaleCount { get; set; }

    public decimal Gross { get; set; }

    /// <summary>
    /// Tax, where the source knows it — an offline van sale carries its own split, an online sale's
    /// lives on the SAP invoice. There is no <c>Net</c> here for that reason: <c>Gross - Vat</c> is only
    /// net when every sale in the total is of the first kind.
    /// </summary>
    public decimal Vat { get; set; }

    public decimal AmountPaid { get; set; }
}

public class RouteCustomerSaleLineModel
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

public class RouteCustomerSaleModel
{
    public RouteCustomerSaleSource Source { get; set; }

    public string Reference { get; set; } = string.Empty;

    public DateTime SoldAt { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal Total { get; set; }

    public decimal VatAmount { get; set; }

    public decimal AmountPaid { get; set; }

    public string? PaymentMethod { get; set; }

    public string? PaymentReference { get; set; }

    public string Status { get; set; } = string.Empty;

    public int? SapDocNum { get; set; }

    public int? SapDocEntry { get; set; }

    public DateTime? PostedAt { get; set; }

    public int? ReceiptGlobalNo { get; set; }

    public string? FiscalDayNo { get; set; }

    public string? FiscalVerificationCode { get; set; }

    public string? FiscalDeviceNumber { get; set; }

    public string? WarehouseCode { get; set; }

    public List<RouteCustomerSaleLineModel> Lines { get; set; } = [];
}

public class RouteCustomerOrderModel
{
    public int Id { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public DateTime OrderDate { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? Currency { get; set; }

    public decimal DocTotal { get; set; }

    public int LineCount { get; set; }

    public bool IsInvoiced { get; set; }

    public int? SapDocNum { get; set; }

    public bool IsAwaitingPricing { get; set; }
}

public class RouteCustomerProductMixRowModel
{
    public string ItemCode { get; set; } = string.Empty;

    public string? ItemDescription { get; set; }

    public decimal Quantity { get; set; }

    public string? UoMCode { get; set; }

    public int LineCount { get; set; }

    public int CustomerCount { get; set; }

    public DateTime? LastSoldAt { get; set; }

    public List<RouteCustomerSalesTotalsModel> TotalsByCurrency { get; set; } = [];
}

public class RouteCustomerSalesRowModel
{
    /// <summary>Null once the customer record has been deleted; the code and name below outlive it.</summary>
    public int? RouteCustomerId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string AssignedBusinessPartnerCode { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? CapturedAt { get; set; }

    public string? Phone { get; set; }

    public int SaleCount { get; set; }

    public int LineCount { get; set; }

    public DateTime? FirstSaleAt { get; set; }

    public DateTime? LastSaleAt { get; set; }

    /// <summary>Null means never bought, which is not the same finding as a long gap.</summary>
    public int? DaysSinceLastSale { get; set; }

    public int OpenOrderCount { get; set; }

    public List<RouteCustomerSalesTotalsModel> TotalsByCurrency { get; set; } = [];
}

public class RouteSalesGroupModel
{
    public string AssignedBusinessPartnerCode { get; set; } = string.Empty;

    public int CustomerCount { get; set; }

    public int SaleCount { get; set; }

    public int NeverBoughtCount { get; set; }

    public int DormantCount { get; set; }

    public List<RouteCustomerSalesTotalsModel> TotalsByCurrency { get; set; } = [];

    public List<RouteCustomerSalesRowModel> Customers { get; set; } = [];
}

public class RouteCustomerSalesSummaryModel
{
    public DateTime From { get; set; }

    public DateTime To { get; set; }

    public int DormantDays { get; set; }

    public List<RouteSalesGroupModel> Routes { get; set; } = [];
}

public class RouteCustomerSalesDetailModel
{
    public RouteCustomerModel Customer { get; set; } = new();

    public DateTime From { get; set; }

    public DateTime To { get; set; }

    public int SaleCount { get; set; }

    public int LineCount { get; set; }

    public DateTime? FirstSaleAt { get; set; }

    public DateTime? LastSaleAt { get; set; }

    public int? DaysSinceLastSale { get; set; }

    public List<RouteCustomerSalesTotalsModel> TotalsByCurrency { get; set; } = [];

    public List<RouteCustomerSaleModel> Sales { get; set; } = [];

    public List<RouteCustomerOrderModel> Orders { get; set; } = [];

    public List<RouteCustomerProductMixRowModel> ProductMix { get; set; } = [];
}

public class RouteCustomerProductMixModel
{
    public DateTime From { get; set; }

    public DateTime To { get; set; }

    public string? AssignedBusinessPartnerCode { get; set; }

    public int? RouteCustomerId { get; set; }

    public List<RouteCustomerProductMixRowModel> Items { get; set; } = [];
}
