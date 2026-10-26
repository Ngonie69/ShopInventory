using System.Text.Json;

namespace ShopInventory.Features.Email;

/// <summary>
/// How the email queue stores a list of addresses: a JSON array.
/// </summary>
/// <remarks>
/// Password reset emails used to be queued with a bare, comma-separated address instead, so
/// <see cref="Parse"/> still reads that form for rows written before the two queue paths agreed.
/// </remarks>
public static class EmailQueueAddresses
{
    public static string Serialize(IEnumerable<string> addresses) =>
        JsonSerializer.Serialize(addresses.ToList());

    public static List<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return [];

        var trimmed = stored.Trim();
        var addresses = trimmed.StartsWith('[')
            ? JsonSerializer.Deserialize<List<string>>(trimmed) ?? []
            : [.. trimmed.Split([',', ';'])];

        return addresses
            .Select(address => address.Trim())
            .Where(address => address.Length > 0)
            .ToList();
    }
}
