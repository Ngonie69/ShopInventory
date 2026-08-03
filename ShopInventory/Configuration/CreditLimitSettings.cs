namespace ShopInventory.Configuration;

/// <summary>
/// Controls the credit limit check that runs before a sales order is accepted or posted to SAP.
/// </summary>
public sealed class CreditLimitSettings
{
    public const string SectionName = "CreditLimit";

    /// <summary>
    /// Turns the check off entirely. Leave enabled — SAP's own approval procedure holds the
    /// document rather than rejecting it, which is what this exists to prevent.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Counts sales orders already raised but not yet invoiced (SAP's "Orders" figure on the BP
    /// master) toward exposure. Off: a customer is over the limit when what it actually owes —
    /// <c>OCRD.Balance</c> — is over, which is the figure credit control works from and the one SAP's
    /// own approval query uses. Orders in flight cannot quietly become debt behind that, because SAP
    /// will not raise an invoice for a customer that is over its limit, so the balance cannot grow
    /// past the limit through an order this check let through. Turning it on stops those orders at
    /// capture instead, at the cost of refusing accounts that owe nothing near their limit.
    /// </summary>
    public bool IncludeOpenOrders { get; set; } = false;

    /// <summary>
    /// Runs the evening sweep for accounts already over their limit. Independent of
    /// <see cref="Enabled"/> only in the sense that both must be on — there is no point reviewing
    /// limits that are not being enforced.
    /// </summary>
    public bool EveningReviewEnabled { get; set; } = true;

    /// <summary>
    /// When the evening review runs, CAT. Defaults to 19:15 — after 19:00, and clear of the 18:00
    /// end-of-day consolidation so the whole-customer-base SAP sweep has the connection to itself.
    /// </summary>
    public string ReviewTimeCAT { get; set; } = "19:15";

    /// <summary>
    /// How many accounts the review notification names before it summarises the rest. The full
    /// list always goes to the log.
    /// </summary>
    public int ReviewNotificationAccountLimit { get; set; } = 10;

    /// <summary>
    /// How long the on-demand over-limit list is served from cache before SAP is read again. Kept
    /// short because balances move as payments post, and a stale list has credit control chasing
    /// accounts that have already paid.
    /// </summary>
    public int ReviewCacheMinutes { get; set; } = 10;
}
