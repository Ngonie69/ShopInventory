namespace ShopInventory.Web.Features.Reports.Queries.GetCounterMonthOnMonth;

/// <summary>
/// Mirrors the API's <c>CounterMonthOnMonthResult</c> (GET api/DesktopIntegration/sales/month-on-month).
/// Nullability matches the API's, or a null would throw on deserialise.
/// </summary>
public sealed record CounterMonthOnMonthData(
    DateTime PreviousMonth,
    DateTime CurrentMonth,
    List<CounterPlaceRow> Places,
    List<CounterTenderRow> Tenders,
    List<CounterItemRow> Items);

public sealed record CounterPlaceRow(
    string Business,
    string WarehouseCode,
    string? ShopName,
    DateTime? FirstSaleDate,
    int PreviousTradingDays,
    int CurrentTradingDays);

public sealed record CounterTenderRow(
    DateTime Month,
    string Business,
    string WarehouseCode,
    string Currency,
    string Tender,
    int Sales,
    decimal Gross,
    decimal Vat,
    int CreditedSales,
    decimal CreditedAmount);

public sealed record CounterItemRow(
    DateTime Month,
    string Business,
    string WarehouseCode,
    string Currency,
    string ItemCode,
    string? ItemName,
    decimal Quantity,
    decimal NetValue,
    decimal CreditedQuantity,
    decimal CreditedNetValue);
