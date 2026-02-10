namespace ShopInventory.Models.Revmax;

/// <summary>
/// Item line for fiscal transactions.
/// Field names MUST match REVMax API exactly.
/// </summary>
public class RevmaxItem
{
    /// <summary>
    /// Line number / header.
    /// </summary>
    public string? HH { get; set; }

    /// <summary>
    /// Item code (required).
    /// </summary>
    public string? ITEMCODE { get; set; }

    /// <summary>
    /// Item name line 1.
    /// </summary>
    public string? ITEMNAME1 { get; set; }

    /// <summary>
    /// Item name line 2.
    /// </summary>
    public string? ITEMNAME2 { get; set; }

    /// <summary>
    /// Quantity (must be > 0).
    /// </summary>
    public decimal QTY { get; set; }

    /// <summary>
    /// Unit price (VAT inclusive, must be >= 0).
    /// </summary>
    public decimal PRICE { get; set; }

    /// <summary>
    /// Line amount. MUST be calculated as QTY × PRICE.
    /// </summary>
    public decimal AMT { get; set; }

    /// <summary>
    /// Tax ID code.
    /// </summary>
    public string? TAX { get; set; }

    /// <summary>
    /// Tax rate as decimal (e.g., 0.155 for 15.5%).
    /// </summary>
    public decimal TAXR { get; set; }
}

/// <summary>
/// Currency received for payment.
/// </summary>
public class CurrencyReceived
{
    /// <summary>
    /// Currency name/code.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Amount received in this currency.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Exchange rate.
    /// </summary>
    public decimal Rate { get; set; }
}
