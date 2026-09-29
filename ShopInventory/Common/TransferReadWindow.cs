namespace ShopInventory.Common;

/// <summary>
/// The date window a warehouse's transfer and transfer-request lists are read over when the caller
/// does not name one.
/// </summary>
/// <remarks>
/// Both lists used to be unbounded in different ways. The transfer history read every transfer the
/// warehouse had ever made, with lines. The request list sent no page size, so SAP answered with its
/// default 20 rows and a shop saw only its latest 20 requests, silently, however many it had raised.
/// Ninety days covers the history screens; a caller that wants more passes its own dates.
/// </remarks>
public static class TransferReadWindow
{
    public const int DefaultDays = 90;

    /// <summary>
    /// The caller's dates, or the last <see cref="DefaultDays"/> days through <paramref name="today"/>
    /// for whichever is missing. Given in the wrong order they are swapped rather than answered with nothing.
    /// </summary>
    public static (DateTime From, DateTime To) Resolve(DateTime? from, DateTime? to, DateTime today)
    {
        var end = (to ?? today).Date;
        var start = (from ?? end.AddDays(-DefaultDays)).Date;
        return start <= end ? (start, end) : (end, start);
    }
}
