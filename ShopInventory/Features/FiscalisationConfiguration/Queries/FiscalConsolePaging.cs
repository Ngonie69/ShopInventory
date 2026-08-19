namespace ShopInventory.Features.FiscalisationConfiguration.Queries;

/// <summary>
/// The one place the fiscalisation console's paging is bounded.
/// </summary>
/// <remarks>
/// A page number arrives from a query string, so it arrives unbounded. Left that way it does two things,
/// neither of them a 400: <c>page * pageSize</c> overflows to a negative <c>Take</c> or <c>Skip</c> and
/// the request 500s, and short of overflow a merely large page makes the work queue read that many rows
/// out of each of its two sources before it can interleave them.
///
/// Clamping rather than refusing, because a page past the end is not an error a person made — it is the
/// list having shrunk since it was rendered, which on a queue whose whole purpose is to reach zero is the
/// normal case. The clamped page is the one reported back, so the pager tells the truth about where it is.
/// </remarks>
internal static class FiscalConsolePaging
{
    /// <summary>Rows one request may ask for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// The deepest row any page may reach. It is the real bound on the work queue's cost, because that
    /// query reads <c>page * pageSize</c> rows from each source to page across both honestly.
    /// </summary>
    public const int MaxReach = 5000;

    public static (int Page, int PageSize) Clamp(int page, int pageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var lastPage = Math.Max(1, MaxReach / size);

        return (Math.Clamp(page, 1, lastPage), size);
    }
}
