namespace ShopInventory.Features.VendingCashUps;

/// <summary>Where a cash-up stands. Stored as text on <c>VendingCashUps.Status</c>.</summary>
public static class VendingCashUpStatuses
{
    /// <summary>Counted and saved, waiting for an Admin.</summary>
    public const string Submitted = "Submitted";

    /// <summary>An Admin sent it back with a question. Saving it again puts it back to Submitted.</summary>
    public const string Queried = "Queried";

    /// <summary>An Admin accepted it. Nothing about it can change after this.</summary>
    public const string Approved = "Approved";
}
