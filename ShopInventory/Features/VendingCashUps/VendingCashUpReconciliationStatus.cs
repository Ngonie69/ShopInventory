namespace ShopInventory.Features.VendingCashUps;

/// <summary>The statuses the reconciliation shows that are not stored on a cash-up.</summary>
public static class VendingCashUpReconciliationStatus
{
    /// <summary>The cashier sold that day and nobody has counted their drawer. Has no variance.</summary>
    public const string NotCashedUp = "NotCashedUp";
}
