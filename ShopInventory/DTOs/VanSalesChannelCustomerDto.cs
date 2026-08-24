using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// One customer in a trade channel, as the handset lists it.
/// </summary>
/// <remarks>
/// Narrower than <see cref="BusinessPartnerDto"/> and named in the handset's own snake_case, matching
/// every other <c>/vansales</c> payload. The code is the only field the next call needs; the rest is
/// what a person needs to recognise the shop in a list of 157.
/// </remarks>
public sealed class VanSalesChannelCustomerDto
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = string.Empty;

    /// <summary>What the account owes, as SAP last computed it.</summary>
    [JsonPropertyName("balance")]
    public decimal Balance { get; set; }

    /// <summary>False for an account frozen in SAP, which the list says rather than hides.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; }
}
