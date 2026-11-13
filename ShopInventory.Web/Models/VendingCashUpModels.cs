namespace ShopInventory.Web.Models;

// Mirrors of ShopInventory/Features/VendingCashUps/VendingCashUpDtos.cs, checked by hand. A counted
// figure is nullable on both sides: null is "not counted", and must never be read as zero.

/// <summary>The statuses a cash-up and the reconciliation report.</summary>
public static class VendingCashUpStatus
{
    public const string NotCashedUp = "NotCashedUp";
    public const string Submitted = "Submitted";
    public const string Queried = "Queried";
    public const string Approved = "Approved";
}

public sealed class VendingCashUpSheetModel
{
    public int? CashUpId { get; set; }
    public int Revision { get; set; }
    public string Status { get; set; } = VendingCashUpStatus.NotCashedUp;
    public Guid CashierUserId { get; set; }
    public string CashierUsername { get; set; } = string.Empty;
    public string CashierName { get; set; } = string.Empty;
    public string? BusinessPartnerCode { get; set; }
    public string? DepotName { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public DateTime BusinessDate { get; set; }
    public List<VendingCashUpCurrencyModel> Currencies { get; set; } = new();
    public List<VendingCashUpTenderModel> Tenders { get; set; } = new();
    public string? VarianceReason { get; set; }
    public string? Note { get; set; }
    public bool Balanced { get; set; }
    public bool NeedsReason { get; set; }
    public bool SalesChangedSinceCount { get; set; }
    public bool SavedByCaller { get; set; }
    public string? QueryNote { get; set; }
    public string? QueriedByName { get; set; }
    public DateTime? QueriedAt { get; set; }
    public string? CapturedByName { get; set; }
    public DateTime? CapturedAt { get; set; }
    public string? UpdatedByName { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

public sealed class VendingCashUpCurrencyModel
{
    public string Currency { get; set; } = string.Empty;
    public decimal ExpectedCash { get; set; }
    public int ExpectedCashSales { get; set; }
    public decimal? CountedExpectedCash { get; set; }
    public decimal? OpeningFloat { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? Banked { get; set; }
    public string? DepositReference { get; set; }
    public decimal? Variance { get; set; }
    public decimal? ToBank { get; set; }
    public List<VendingCashUpDenominationModel> Denominations { get; set; } = new();
}

public sealed class VendingCashUpTenderModel
{
    public string Currency { get; set; } = string.Empty;
    public string Tender { get; set; } = string.Empty;
    public decimal Expected { get; set; }
    public int ExpectedSales { get; set; }
    public decimal? CountedExpected { get; set; }
    public decimal? Declared { get; set; }
    public decimal? Variance { get; set; }
}

public sealed class VendingCashUpDenominationModel
{
    public decimal Value { get; set; }
    public int Quantity { get; set; }
}

public sealed class VendingReconciliationModel
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<VendingReconciliationRowModel> Rows { get; set; } = new();
    public List<VendingReconciliationTotalModel> Totals { get; set; } = new();
    public VendingReconciliationCountsModel Counts { get; set; } = new();
}

public sealed class VendingReconciliationRowModel
{
    public int? CashUpId { get; set; }
    public int Revision { get; set; }
    public string Status { get; set; } = VendingCashUpStatus.NotCashedUp;
    public DateTime BusinessDate { get; set; }
    public Guid CashierUserId { get; set; }
    public string CashierUsername { get; set; } = string.Empty;
    public string CashierName { get; set; } = string.Empty;
    public string? BusinessPartnerCode { get; set; }
    public string? DepotName { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public List<VendingReconciliationCurrencyModel> Currencies { get; set; } = new();
    public bool Balanced { get; set; }
    public bool NeedsReason { get; set; }
    public bool SalesChangedSinceCount { get; set; }
    public string? VarianceReason { get; set; }
    public string? QueryNote { get; set; }
    public string? CapturedByName { get; set; }
    public DateTime? CapturedAt { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public bool IsCounted => CashUpId is not null;
}

public sealed class VendingReconciliationCurrencyModel
{
    public string Currency { get; set; } = string.Empty;
    public decimal Invoiced { get; set; }
    public int Sales { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal? OpeningFloat { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? Accounted { get; set; }
    public decimal? Banked { get; set; }
    public decimal? Variance { get; set; }
}

public sealed class VendingReconciliationTotalModel
{
    public string Currency { get; set; } = string.Empty;
    public decimal Invoiced { get; set; }
    public decimal InvoicedOnCountedDays { get; set; }
    public decimal Accounted { get; set; }
    public decimal Banked { get; set; }
    public decimal NetVariance { get; set; }
    public decimal OpenShortfall { get; set; }
}

public sealed class VendingReconciliationCountsModel
{
    public int CashierDays { get; set; }
    public int NotCashedUp { get; set; }
    public int AwaitingApproval { get; set; }
    public int Queried { get; set; }
    public int Approved { get; set; }
    public int BalancedOnFirstCount { get; set; }
    public int NeedsReason { get; set; }
}

/// <summary>A count as the portal submits it to <c>PUT api/vending/cash-ups</c>.</summary>
public sealed class SaveVendingCashUpRequestModel
{
    public Guid CashierUserId { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public DateTime BusinessDate { get; set; }
    public int ExpectedRevision { get; set; }
    public List<SaveVendingCashUpCurrencyModel> Currencies { get; set; } = new();
    public List<SaveVendingCashUpTenderModel> Tenders { get; set; } = new();
    public string? VarianceReason { get; set; }
    public string? Note { get; set; }
}

public sealed class SaveVendingCashUpCurrencyModel
{
    public string Currency { get; set; } = string.Empty;
    public decimal OpeningFloat { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal Banked { get; set; }
    public string? DepositReference { get; set; }
    public List<VendingCashUpDenominationModel>? Denominations { get; set; }
}

public sealed class SaveVendingCashUpTenderModel
{
    public string Currency { get; set; } = string.Empty;
    public string Tender { get; set; } = string.Empty;
    public decimal Declared { get; set; }
}
