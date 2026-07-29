namespace ShopInventory.Common.Caching;

/// <summary>
/// Identifies one resolved unit of measure: the item, and the UoM the order line asked for.
/// </summary>
/// <param name="ItemCode">Normalised item code.</param>
/// <param name="RequestedUomCode">
/// Normalised requested UoM, or empty when the line named none. Part of the key because the same
/// item resolves differently depending on what was asked for.
/// </param>
public readonly record struct SapItemUomKey(string ItemCode, string RequestedUomCode)
{
    /// <summary>
    /// Builds a key in its canonical form. Item and UoM codes are matched case-insensitively
    /// throughout the SAP client but Postgres compares text case-sensitively, so normalising here
    /// — rather than at each call site — is what keeps a lookup able to find what a save wrote.
    /// </summary>
    public static SapItemUomKey For(string itemCode, string? requestedUomCode)
        => new(
            itemCode.Trim().ToUpperInvariant(),
            (requestedUomCode ?? string.Empty).Trim().ToUpperInvariant());
}

/// <summary>
/// Durable store of item UoM resolutions, shared across restarts and API nodes.
/// </summary>
/// <remarks>
/// Resolving a UoM from SAP costs several SQLQueries round-trips per batch of items, each of
/// which queues behind every other SAP call the process is making. Persisting the answers keeps
/// that cost off the approval path once an item has been seen.
/// </remarks>
public interface ISapItemUomMappingStore
{
    /// <summary>
    /// Reads the stored resolutions for the requested keys. Keys with no stored resolution are
    /// simply absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>> GetAsync(
        IReadOnlyCollection<SapItemUomKey> keys,
        CancellationToken cancellationToken);

    /// <summary>
    /// Upserts resolutions. Safe to call concurrently for the same key from several nodes: the
    /// resolutions are derived from the same SAP data, so a conflict means both writers agree.
    /// Never throws — a failure to persist costs a re-resolution later, and must not fail a post.
    /// </summary>
    Task SaveAsync(
        IReadOnlyCollection<(SapItemUomKey Key, string? UoMCode, int UoMEntry)> mappings,
        CancellationToken cancellationToken);
}
