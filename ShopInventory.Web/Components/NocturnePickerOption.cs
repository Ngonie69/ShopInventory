namespace ShopInventory.Web.Components;

/// <summary>
/// One row in a <see cref="NocturnePicker"/> menu.
/// </summary>
/// <remarks>
/// Shaped for the thing this picker is for: choosing one record out of thousands
/// that is identified by a code. So the code is both the bound value and a line
/// the row draws, because in this app the code is what disambiguates two
/// otherwise identical names — SAP keeps one business partner per currency, so
/// "Abbiamo Trading Deli Spices FCA" appears twice and only ABB001 (FCA) against
/// ABB001 (ZiG) tells them apart. A picker that showed names alone would offer
/// the operator two rows they cannot choose between.
/// </remarks>
/// <param name="Value">
/// The code. Bound out on selection, drawn under the label, and searched.
/// </param>
/// <param name="Label">The name — the row's first line, and what the closed trigger reads.</param>
/// <param name="Hint">
/// Trailing muted text: a currency, a group, "Inactive". Searched too, so typing
/// "usd" finds the USD partners whose code carries no suffix.
/// </param>
public sealed record NocturnePickerOption(string Value, string Label, string? Hint = null);
