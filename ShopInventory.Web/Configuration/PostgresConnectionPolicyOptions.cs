namespace ShopInventory.Web.Configuration;

public sealed class PostgresConnectionPolicyOptions
{
    public const string SectionName = "PostgresConnectionPolicy";

    public bool EnforceRemoteHostInProduction { get; set; }

    public bool RequireReadWriteTargetForMultiHost { get; set; }
}