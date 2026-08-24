namespace ShopInventory.Common.Mobile;

/// <summary>
/// Which handset accounts may look outside their own route, at a whole trade channel.
/// </summary>
/// <remarks>
/// <para>Every other customer read a handset makes is scoped to the signed-in rep — either their route
/// customers (<see cref="VanSalesRouteCustomerScope"/>) or their assigned codes
/// (<see cref="MobileAssignedCustomerScope"/>). A channel is not: 'General Trade' is 157 customers
/// across the company, most of them on somebody else's route.</para>
///
/// <para>So this is the one place that says who is allowed to cross that line, and it is deliberately
/// a short list. The cost of being wrong is asymmetric but not symmetric with the invoicing gates: no
/// document is raised and no stock moves, so the exposure is one rep reading another's trading
/// history rather than a fiscalised document that needs a credit note. It is still a real exposure,
/// which is why it is a named rule with a test rather than a role string compared at the call site.</para>
///
/// <para>Kept free of EF and HTTP so both halves can be asserted on a build agent. The caller supplies
/// the role; reading the signed-in user is the handler's job.</para>
/// </remarks>
public static class ChannelCustomerAccess
{
    /// <summary>
    /// The only channel the handset asks for today, spelled as SAP holds it in <c>OCRD.U_Channel</c>.
    /// </summary>
    /// <remarks>
    /// Verified against both company databases rather than assumed — <c>db_Alpha(250)</c>, present on
    /// OCRD in <c>KEFALOS_TEST_3</c> and <c>KEFALOS_USD_NEW2</c>. The other values in production are
    /// Retail, Wholesale, Factory Direct, Food Service, QSR, Export, Promotions, Internal, Vending,
    /// Shops, Distributors and Other, plus 117 customers carrying none — so offering a second channel
    /// later is a matter of widening this, not of finding the data.
    ///
    /// <para>Matched exactly at the server. 'General Trade' and 'Trade' are different books of
    /// customers and a rep shown one for the other has no way to tell.</para>
    /// </remarks>
    public const string GeneralTrade = "General Trade";

    /// <summary>
    /// Whether <paramref name="role"/> may list a whole channel's customers and read their invoices.
    /// </summary>
    /// <remarks>
    /// Trimmed and case-folded because the role is the server's own column and this app has no say in
    /// how it is spelled — the same reason the handset's <c>UserRole.Parse</c> does it. A role this
    /// method does not recognise is refused, so a new role added at the server has to be named here to
    /// gain the view rather than falling into it.
    /// </remarks>
    public static bool MaySeeWholeChannel(string? role)
    {
        var named = (role ?? string.Empty).Trim();

        return named.Equals(Models.ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase)
            || named.Equals(Models.ApplicationRoles.StockController, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What a refused account is told. One sentence, because a handset shows it in a dialog.</summary>
    public static string Refusal =>
        "This account may only see the customers on its own route. Channel-wide customer lists are for "
        + "stock controllers and administrators.";
}
