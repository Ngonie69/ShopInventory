using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShopInventory.Common.Idempotency;

/// <summary>
/// The fingerprint of a request, for telling a genuine retry from a key used twice for two things.
/// </summary>
/// <remarks>
/// Extracted so that everything guarding one idempotency key makes the <em>same</em> comparison. That
/// sounds like tidiness and is not: a desktop sale is guarded twice, by
/// <see cref="IdempotencyRequestStore"/> and by the unique index on
/// <c>DesktopSaleEntity.ExternalReferenceId</c>, and until 10 September 2026 only the first of them
/// looked at the payload at all.
///
/// <para>
/// The store's record expires — sixty minutes by default — while the sale it guards does not. So a
/// key re-used within the hour was correctly refused, and the same key re-used the next morning fell
/// through to a lookup that compared nothing and answered with the previous day's invoice. A till
/// printed it, banked it and deducted stock against it. Two guards on one key, disagreeing about
/// what the key means, is the defect; one hash function is how they are held to the same answer.
/// </para>
/// </remarks>
public static class IdempotencyRequestHash
{
    /// <remarks>
    /// <see cref="JsonSerializerDefaults.Web"/> to match every other serialization on this path. The
    /// hash is only ever compared against another hash produced here, so the format matters only in
    /// that it must not change underneath stored values.
    /// </remarks>
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The hash of <paramref name="request"/> as serialized for storage.</summary>
    public static string Of(object request) =>
        Of(JsonSerializer.Serialize(request, SerializerOptions));

    /// <summary>The hash of an already-serialized request.</summary>
    public static string Of(string serialized) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
}
