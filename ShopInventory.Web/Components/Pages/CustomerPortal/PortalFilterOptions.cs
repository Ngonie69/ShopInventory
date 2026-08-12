using ShopInventory.Web.Components;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages.CustomerPortal;

/// <summary>
/// The option lists the portal's filter dropdowns are built from.
/// </summary>
/// <remarks>
/// The account filter is the same control on four pages — invoices, payments, PODs and
/// the item summary — and it was four copies of the same loop, which had already drifted
/// (one of them capitalised "All Accounts"). One definition instead, so the label a
/// customer sees for an account cannot depend on which page they are standing on.
/// </remarks>
public static class PortalFilterOptions
{
    /// <summary>
    /// The linked accounts a multi-account login reaches, headed by the "no filter" row.
    /// </summary>
    /// <remarks>
    /// The currency rides in the label because it changes what the numbers beside it mean;
    /// the card code is a hint, which the menu sets in muted type at the end of the row.
    /// No family on any row: an account carries no state beyond its name, and a swatch on
    /// the "All" row alone would read as a mistake.
    /// </remarks>
    public static IEnumerable<NocturneSelectOption<string>> Accounts(
        IEnumerable<LinkedAccountInfo> accounts,
        string allLabel = "All accounts") =>
        accounts
            .Select(account => new NocturneSelectOption<string>(
                account.CardCode,
                string.IsNullOrEmpty(account.Currency)
                    ? account.CardName
                    : $"{account.CardName} ({account.Currency})")
            {
                Hint = account.CardCode
            })
            .Prepend(All(allLabel));

    /// <summary>
    /// An "All …" row with no swatch, ruled off from the rest.
    /// </summary>
    /// <remarks>
    /// <see cref="NocturneSelectOption.All"/> gives the row the neutral family, whose
    /// slate is not a colour in the portal's palette — and these lists carry no families
    /// of their own for it to sit alongside.
    /// </remarks>
    public static NocturneSelectOption<string> All(string label) =>
        new(string.Empty, label) { RuleAfter = true, IsUnset = true };
}
