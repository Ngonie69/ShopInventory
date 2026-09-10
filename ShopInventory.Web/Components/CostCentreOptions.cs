using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components;

/// <summary>
/// Turns cost centres into <see cref="NocturnePicker"/> rows.
/// </summary>
/// <remarks>
/// Two call sites — /shops and the van sales field on /user-management — and the
/// same argument as <see cref="BusinessPartnerOptions"/>: one place, so the
/// three judgements below cannot drift apart.
/// </remarks>
public static class CostCentreOptions
{
    /// <summary>
    /// The rows, ordered by code. Centres with no code are dropped: the code is
    /// what the picker binds out, so a blank one is a row that cannot be chosen.
    /// An inactive centre is kept and marked rather than filtered out — callers
    /// that load only active ones simply never see the hint, and for one that
    /// does not, dropping a row is how a field that was set reads as unset.
    /// </summary>
    public static List<NocturnePickerOption> ByCode(IEnumerable<CostCentreDto>? centres) =>
        (centres ?? [])
            .Where(centre => !string.IsNullOrWhiteSpace(centre.CenterCode))
            .Select(centre => new NocturnePickerOption(
                centre.CenterCode!,
                string.IsNullOrWhiteSpace(centre.CenterName) ? centre.CenterCode! : centre.CenterName,
                centre.IsActive ? null : "Inactive"))
            .OrderBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
