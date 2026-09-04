using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components;

/// <summary>
/// Turns the cached business partners into <see cref="NocturnePicker"/> rows.
/// </summary>
/// <remarks>
/// One place rather than a copy per page, because the three judgements below are
/// the kind that drift apart the moment they are made twice — and there are
/// already two callers, /shops and the van sales field on /user-management.
/// </remarks>
public static class BusinessPartnerOptions
{
    /// <summary>
    /// SAP's marker for a partner that may be billed in any currency. It is not
    /// a currency code, so drawn in the row's hint slot it reads as noise beside
    /// USD and ZiG — and it is the same two characters on every such partner, so
    /// it separates nothing.
    /// </summary>
    private const string AnyCurrency = "##";

    /// <summary>
    /// The rows, ordered by code. Partners with no code are dropped: the code is
    /// the value the picker binds out, so a blank one is a row that cannot be
    /// chosen.
    /// </summary>
    public static List<NocturnePickerOption> From(IEnumerable<BusinessPartnerDto>? partners) =>
        (partners ?? [])
            .Where(partner => !string.IsNullOrWhiteSpace(partner.CardCode))
            .Select(partner => new NocturnePickerOption(
                partner.CardCode!,
                string.IsNullOrWhiteSpace(partner.CardName) ? partner.CardCode! : partner.CardName,
                Hint(partner)))
            .OrderBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The trailing note. "Inactive" outranks the currency because it is the one
    /// that should stop someone choosing the row; the currency is next because
    /// SAP keeps one partner per currency, so it is often the only thing telling
    /// two identically named rows apart.
    /// </summary>
    private static string? Hint(BusinessPartnerDto partner)
    {
        if (!partner.IsActive)
        {
            return "Inactive";
        }

        var currency = partner.Currency?.Trim();

        return string.IsNullOrEmpty(currency) || currency == AnyCurrency ? null : currency;
    }
}
