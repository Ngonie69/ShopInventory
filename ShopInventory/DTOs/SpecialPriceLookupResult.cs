namespace ShopInventory.DTOs;

/// <summary>
/// The special prices found for a business partner, plus whether the lookup actually finished.
/// </summary>
/// <remarks>
/// The distinction matters because an empty result is ambiguous on its own: "this customer has no
/// special prices" and "SAP could not tell me" produce the same dictionary but mean opposite
/// things for pricing. On 2026-07-27 a failed lookup for TMP119 returned empty, was logged as
/// "Retrieved 0 active special prices", and order 2209 posted to SAP at full list price — while
/// the same customer had ten active special prices three minutes earlier.
/// </remarks>
/// <param name="Prices">Active special prices by item code. Never null; may legitimately be empty.</param>
/// <param name="IsComplete">
/// False when the lookup failed part-way. <paramref name="Prices"/> may then hold some of the
/// customer's special prices, none of them, or all of them — there is no way to know which.
/// </param>
public readonly record struct SpecialPriceLookupResult(
    Dictionary<string, decimal> Prices,
    bool IsComplete);
