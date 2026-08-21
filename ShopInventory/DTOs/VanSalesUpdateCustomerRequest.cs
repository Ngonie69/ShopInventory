using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// The parts of a route customer a handset may correct.
/// </summary>
/// <remarks>
/// Deliberately narrower than <see cref="UpdateRouteCustomerRequest"/>, which an administrator uses.
/// Three of that request's fields are not a rep's to change and are absent here rather than ignored:
/// the route, because moving a shop to another van from a handset is not a correction; the code,
/// because it is the identity every sale and the handset's own queue names the shop by; and the
/// active flag, because that is the removal, which has its own permission and its own audience.
///
/// What is left is what a rep actually finds wrong at the counter — a trading name spelt from
/// hearing it, a phone number that has changed hands, an address that was never filled in.
/// </remarks>
public class VanSalesUpdateCustomerRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("vat_number")]
    public string? VatNumber { get; set; }
}
