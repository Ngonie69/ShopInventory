namespace ShopInventory.Configuration;

public sealed class PostgresConnectionPolicyOptions
{
    public const string SectionName = "PostgresConnectionPolicy";

    public bool EnforceRemoteHostInProduction { get; set; }

    public bool RequireReadWriteTargetForMultiHost { get; set; }

    /// <summary>
    /// The most connections this process may pool, whatever the connection string asks for. Null leaves
    /// the string's own value alone.
    /// </summary>
    public int? MaximumPoolSize { get; set; }

    /// <summary>
    /// The most idle connections this process keeps open, whatever the connection string asks for. Null
    /// leaves the string's own value alone.
    /// </summary>
    public int? MinimumPoolSize { get; set; }
}