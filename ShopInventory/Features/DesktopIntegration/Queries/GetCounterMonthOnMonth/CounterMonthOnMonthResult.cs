namespace ShopInventory.Features.DesktopIntegration.Queries.GetCounterMonthOnMonth;

/// <summary>
/// The raw figures of a month-on-month report: everything is per month, per place and per currency, so
/// the caller can group items its own way (product groups, SAP item groups) and convert currencies.
/// </summary>
/// <param name="PreviousMonth">First day of the earlier month.</param>
/// <param name="CurrentMonth">First day of the later month.</param>
public sealed record CounterMonthOnMonthResult(
    DateTime PreviousMonth,
    DateTime CurrentMonth,
    IReadOnlyList<CounterPlaceRow> Places,
    IReadOnlyList<CounterTenderRow> Tenders,
    IReadOnlyList<CounterItemRow> Items);

/// <summary>A shop or vending point that sold in either month.</summary>
/// <param name="Business">shops or vending (<c>SaleBusinesses</c>).</param>
/// <param name="ShopName">The place's name: a shop's from the Shops table, when it is a shop on file; a vending
/// point's from its depot's business partner in SAP (KEFBYC → "Cortina Bulawayo Vending").</param>
/// <param name="FirstSaleDate">The first day this place ever sold on a till, so a month before it can be flagged as partial.</param>
public sealed record CounterPlaceRow(
    string Business,
    string WarehouseCode,
    string? ShopName,
    DateTime? FirstSaleDate,
    int PreviousTradingDays,
    int CurrentTradingDays);

/// <summary>Takings of one tender, incl. VAT, and what was credited back of those sales.</summary>
/// <param name="Month">First day of the month.</param>
/// <param name="Tender">Normalised (<c>TenderTypes.ReportingName</c>): Cash, Swipe, Ecocash, Innbucks, Not recorded, ...</param>
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

/// <summary>One item's sales, before VAT, with what was credited back already taken off.</summary>
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
